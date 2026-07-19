using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MoonYa.Services
{
    public class ExecutionResult
    {
        public string Status { get; set; } = "";     // "success", "error", "rejected"
        public string Output { get; set; } = "";      // combined stdout
        public string Error { get; set; } = "";       // stderr or error message
        public int ExitCode { get; set; }
        public long DurationMs { get; set; }
        public string RiskLevel { get; set; } = "";   // "low", "medium", "high"
        public List<string> MatchedRules { get; set; } = new();
        public string FullCommand { get; set; } = "";  // the actual command that was/would be executed
    }

    public class CommandExecutionService
    {
        private const int MaxOutputBytes = 1048576; // 1MB
        private const int BackgroundOutputMaxBytes = 100 * 1024; // 100KB - 后台命令输出缓冲区上限
        private static readonly TimeSpan BackgroundCleanupInterval = TimeSpan.FromMinutes(5); // 后台命令完成后 5 分钟自动清理

        private readonly RiskAssessmentService _riskService;
        private readonly SandboxService _sandboxService;
        private readonly int _commandTimeoutSec;

        // 后台命令进程池：command_id → BackgroundCommand
        private readonly Dictionary<string, BackgroundCommand> _backgroundCommands = new();
        private readonly object _bgLock = new();

        public CommandExecutionService(RiskAssessmentService riskService, SandboxService sandboxService, int commandTimeoutSec = 60)
        {
            _riskService = riskService;
            _sandboxService = sandboxService;
            _commandTimeoutSec = commandTimeoutSec;
        }

        // 后台命令上下文：保存进程、输出缓冲区、状态及清理定时器
        private class BackgroundCommand
        {
            public string CommandId { get; set; } = "";
            public Process? Process { get; set; }
            public StringBuilder OutputBuffer { get; set; } = new();  // 最大 100KB，循环覆盖旧内容
            public DateTime StartedAt { get; set; }
            public DateTime? FinishedAt { get; set; }
            public int? ExitCode { get; set; }
            public string Status { get; set; } = "running";  // running / done / killed
            public Timer? CleanupTimer { get; set; }  // 完成后 5 分钟自动清理
            public string? SandboxDir { get; set; }  // 后台命令独占的沙箱目录，进程退出后清理
        }

        /// <summary>
        /// 主入口：根据 blocking 参数分发到同步或异步执行路径。
        /// blocking=null/true → 走原同步逻辑（风险评估 → 确认 → 沙箱执行），返回 ExecutionResult
        /// blocking=false     → 调用 ExecuteAsyncBackground 立即返回 command_id，不等待完成
        /// 返回类型为 object 以兼容两种模式（ExecutionResult 或后台命令的 {success, command_id, status} 对象）
        /// </summary>
        public async Task<object> ExecuteAsync(string command, bool autoApproveMediumRisk = true, string? cwd = null, int? timeoutSec = null, bool? blocking = null)
        {
            // 后台模式：立即返回 command_id
            if (blocking == false)
            {
                return await ExecuteAsyncBackground(command, cwd, timeoutSec);
            }

            // 1. Pre-check: basic command syntax validation (quote matching)
            var syntaxError = ValidateCommandSyntax(command);
            if (syntaxError != null)
            {
                return new ExecutionResult
                {
                    Status = "error",
                    Error = $"Command syntax error: {syntaxError}",
                    FullCommand = command,
                    RiskLevel = "low"
                };
            }

            // 2. Risk assessment
            var riskResult = _riskService.AssessCommand(command);

            // 3. User confirmation: High risk always confirms; Medium risk only if not auto-approved
            var needsConfirmation = riskResult.Level == RiskLevel.High ||
                (riskResult.Level == RiskLevel.Medium && !autoApproveMediumRisk);
            if (needsConfirmation)
            {
                var confirmed = await ShowConfirmationDialog(command, riskResult);
                if (!confirmed)
                {
                    return new ExecutionResult
                    {
                        Status = "rejected",
                        Output = "",
                        Error = "User rejected the command execution.\n建议：该命令被判定为高风险，请简化命令或拆分为低风险子步骤",
                        FullCommand = command,
                        RiskLevel = riskResult.Level.ToString().ToLower(),
                        MatchedRules = riskResult.MatchedRules
                    };
                }
            }

            // 4. Execute in sandbox
            return await ExecuteInSandboxAsync(command, riskResult, autoApproveMediumRisk, cwd, timeoutSec);
        }

        /// <summary>
        /// 异步启动后台命令：生成 command_id、启动进程、注册输出缓冲与退出回调，立即返回。
        /// 不等待进程完成，长运行命令（如 dev server）通过此方法启动后由 get_command_status 查询状态。
        /// </summary>
        public async Task<object> ExecuteAsyncBackground(string command, string? cwd, int? timeoutSec)
        {
            // 1. 语法校验
            var syntaxError = ValidateCommandSyntax(command);
            if (syntaxError != null)
            {
                return new { success = false, error = $"Command syntax error: {syntaxError}" };
            }

            string? sandboxDir = null;
            try
            {
                // 2. 构造 startInfo（复用 ExecuteInSandboxAsync 的 startInfo 构造逻辑，但不等待完成）
                sandboxDir = _sandboxService.CreateSandboxDirectory();
                var workingDir = (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) ? cwd : sandboxDir;

                bool isPowerShell = command.TrimStart().StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
                                   command.Contains("|") || command.Contains("$") || command.Contains("Get-") ||
                                   command.Contains("Set-") || command.Contains("Write-");

                string shell, scriptPath, arguments;
                if (isPowerShell)
                {
                    var scriptContent = command.TrimStart();
                    if (scriptContent.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        scriptContent = scriptContent.Substring("powershell".Length).TrimStart();
                    }
                    shell = "powershell.exe";
                    scriptPath = Path.Combine(sandboxDir, "script.ps1");
                    await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(false));
                    arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";
                }
                else
                {
                    shell = "cmd.exe";
                    scriptPath = Path.Combine(sandboxDir, "script.bat");
                    await File.WriteAllTextAsync(scriptPath, command + "\r\n", new UTF8Encoding(false));
                    arguments = $"/c \"{scriptPath}\"";
                }

                var startInfo = _sandboxService.CreateSandboxedProcessStartInfo(shell, arguments, workingDir);

                // 3. 生成 command_id（12 位十六进制）
                var commandId = Guid.NewGuid().ToString("N").Substring(0, 12);

                // 4. 创建 BackgroundCommand 上下文
                var bgCmd = new BackgroundCommand
                {
                    CommandId = commandId,
                    StartedAt = DateTime.Now,
                    Status = "running",
                    SandboxDir = sandboxDir
                };

                // 5. 启动进程（EnableRaisingEvents 用于订阅 Exited 事件）
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                bgCmd.Process = process;

                // 输出缓冲：stdout / stderr 都追加到 OutputBuffer，超过 100KB 循环覆盖旧内容
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (_bgLock)
                        {
                            AppendOutput(bgCmd.OutputBuffer, e.Data);
                        }
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (_bgLock)
                        {
                            AppendOutput(bgCmd.OutputBuffer, "[stderr] " + e.Data);
                        }
                    }
                };
                // 进程退出：标记完成状态、记录 ExitCode、启动 5 分钟清理定时器
                process.Exited += (_, _) =>
                {
                    try
                    {
                        lock (_bgLock)
                        {
                            // 仅当未被停止（killed）时才标记为 done
                            if (bgCmd.Status == "running")
                            {
                                bgCmd.Status = "done";
                            }
                            bgCmd.FinishedAt = DateTime.Now;
                            try { bgCmd.ExitCode = process.ExitCode; } catch { bgCmd.ExitCode = -1; }

                            // 启动 5 分钟自动清理定时器
                            bgCmd.CleanupTimer?.Dispose();
                            bgCmd.CleanupTimer = new Timer(_ =>
                            {
                                lock (_bgLock)
                                {
                                    try { bgCmd.Process?.Dispose(); } catch { }
                                    if (bgCmd.SandboxDir != null)
                                    {
                                        _sandboxService.CleanupSandbox(bgCmd.SandboxDir);
                                    }
                                    _backgroundCommands.Remove(commandId);
                                }
                            }, null, BackgroundCleanupInterval, Timeout.InfiniteTimeSpan);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ExecuteAsyncBackground: Exited handler error: {ex.Message}");
                    }
                };

                process.Start();
                // 立即开始异步读取输出，避免 4KB 管道缓冲区被填满导致进程阻塞
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // 关闭 stdin 发送 EOF，防止等待输入的脚本无限挂起
                try { process.StandardInput.Close(); } catch { }

                // 注：后台长运行命令不应用 CPU/内存限制（Job Object），否则 dev server 可能被误杀。
                // 进程退出时通过 Exited 事件清理沙箱目录。

                // 6. 加入进程池
                lock (_bgLock)
                {
                    _backgroundCommands[commandId] = bgCmd;
                }

                // 7. 立即返回 command_id 和 running 状态
                return new
                {
                    success = true,
                    command_id = commandId,
                    status = "running",
                    started_at = bgCmd.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz")
                };
            }
            catch (Exception ex)
            {
                // 启动失败：清理已创建的沙箱目录
                if (sandboxDir != null)
                {
                    try { _sandboxService.CleanupSandbox(sandboxDir); } catch { }
                }
                // 不自动重试（避免重复启动失败，如端口冲突、权限不足、文件不存在），
                // 返回结构化错误 + suggestion 让 AI 决策下一步
                return new
                {
                    success = false,
                    error = $"启动后台命令失败: {ex.Message}",
                    suggestion = "请改用 blocking=true 同步模式重试，或检查命令语法和路径"
                };
            }
        }

        /// <summary>
        /// 查询后台命令状态：返回 running/done/killed、exit_code、当前输出缓冲区内容。
        /// 命令不存在或已被 5 分钟清理定时器回收时返回错误。
        /// </summary>
        public Task<object> GetCommandStatusAsync(string commandId)
        {
            lock (_bgLock)
            {
                if (string.IsNullOrEmpty(commandId) ||
                    !_backgroundCommands.TryGetValue(commandId, out var bgCmd))
                {
                    return Task.FromResult<object>(new { success = false, error = "命令不存在或已被清理" });
                }

                return Task.FromResult<object>(new
                {
                    success = true,
                    command_id = bgCmd.CommandId,
                    status = bgCmd.Status,
                    exit_code = bgCmd.ExitCode,
                    output = bgCmd.OutputBuffer.ToString(),
                    started_at = bgCmd.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    finished_at = bgCmd.FinishedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz")
                });
            }
        }

        /// <summary>
        /// 停止后台命令：调用 Process.Kill(entireProcessTree: true) 终止整个进程树，
        /// 标记 Status=killed，启动 5 分钟清理定时器回收资源。
        /// </summary>
        public Task<object> StopCommandAsync(string commandId)
        {
            try
            {
                lock (_bgLock)
                {
                    if (string.IsNullOrEmpty(commandId) ||
                        !_backgroundCommands.TryGetValue(commandId, out var bgCmd))
                    {
                        return Task.FromResult<object>(new { success = false, error = "命令不存在或已被清理" });
                    }

                    // 终止整个进程树（含子进程，如 npm run dev 启动的 vite 子进程）
                    if (bgCmd.Process != null && !bgCmd.Process.HasExited)
                    {
                        try
                        {
                            bgCmd.Process.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"StopCommandAsync: Kill failed: {ex.Message}");
                        }
                    }

                    bgCmd.Status = "killed";
                    bgCmd.FinishedAt = DateTime.Now;
                    bgCmd.ExitCode = -1;

                    // 启动 5 分钟自动清理定时器
                    bgCmd.CleanupTimer?.Dispose();
                    bgCmd.CleanupTimer = new Timer(_ =>
                    {
                        lock (_bgLock)
                        {
                            try { bgCmd.Process?.Dispose(); } catch { }
                            if (bgCmd.SandboxDir != null)
                            {
                                _sandboxService.CleanupSandbox(bgCmd.SandboxDir);
                            }
                            _backgroundCommands.Remove(commandId);
                        }
                    }, null, BackgroundCleanupInterval, Timeout.InfiniteTimeSpan);

                    return Task.FromResult<object>(new
                    {
                        success = true,
                        command_id = commandId,
                        status = "killed",
                        exit_code = -1
                    });
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult<object>(new { success = false, error = $"停止命令失败: {ex.Message}" });
            }
        }

        /// <summary>
        /// 追加输出到缓冲区，超过 100KB 时循环覆盖旧内容（保留最新 100KB）。
        /// 调用方需持有 _bgLock 以保证线程安全。
        /// </summary>
        private static void AppendOutput(StringBuilder buffer, string data)
        {
            buffer.AppendLine(data);
            if (buffer.Length > BackgroundOutputMaxBytes)
            {
                // 保留最新的 100KB：从头删除超出部分
                var excess = buffer.Length - BackgroundOutputMaxBytes;
                buffer.Remove(0, excess);
            }
        }

        private async Task<ExecutionResult> ExecuteInSandboxAsync(string command, RiskAssessmentResult riskResult, bool autoApproveMediumRisk, string? cwd, int? timeoutSec)
        {
            var sandboxDir = _sandboxService.CreateSandboxDirectory();
            var workingDir = (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) ? cwd : sandboxDir;
            var effectiveTimeout = timeoutSec ?? _commandTimeoutSec;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Determine if it's cmd or powershell based on command prefix or content
                bool isPowerShell = command.TrimStart().StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
                                   command.Contains("|") || command.Contains("$") || command.Contains("Get-") ||
                                   command.Contains("Set-") || command.Contains("Write-");

                string shell, scriptPath, arguments;
                if (isPowerShell)
                {
                    // Strip leading "powershell " prefix if present
                    var scriptContent = command.TrimStart();
                    if (scriptContent.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        scriptContent = scriptContent.Substring("powershell".Length).TrimStart();
                    }
                    shell = "powershell.exe";
                    scriptPath = Path.Combine(sandboxDir, "script.ps1");
                    await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(false));
                    arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";
                }
                else
                {
                    shell = "cmd.exe";
                    scriptPath = Path.Combine(sandboxDir, "script.bat");
                    await File.WriteAllTextAsync(scriptPath, command + "\r\n", new UTF8Encoding(false));
                    arguments = $"/c \"{scriptPath}\"";
                }

                var startInfo = _sandboxService.CreateSandboxedProcessStartInfo(shell, arguments, workingDir);

                var outputTruncated = false;
                var errorTruncated = false;
                using var process = new Process { StartInfo = startInfo };
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null && !outputTruncated)
                    {
                        if (outputBuilder.Length + e.Data.Length > MaxOutputBytes)
                        {
                            outputBuilder.AppendLine("\n[输出已截断，超过 1MB 上限]");
                            outputTruncated = true;
                        }
                        else
                        {
                            outputBuilder.AppendLine(e.Data);
                        }
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && !errorTruncated)
                    {
                        if (errorBuilder.Length + e.Data.Length > MaxOutputBytes)
                        {
                            errorBuilder.AppendLine("\n[输出已截断，超过 1MB 上限]");
                            errorTruncated = true;
                        }
                        else
                        {
                            errorBuilder.AppendLine(e.Data);
                        }
                    }
                };

                process.Start();

                // Start async output reading IMMEDIATELY to drain the pipe buffer.
                // Must happen BEFORE ApplyResourceLimits to avoid a window where the
                // process fills the 4KB pipe buffer and blocks permanently.
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Close stdin immediately to send EOF - prevents scripts waiting for input from hanging forever
                try { process.StandardInput.Close(); } catch { }

                // Apply resource limits via job object
                _sandboxService.ApplyResourceLimits(process, sandboxDir);

                // Wait with timeout
                var completedNormally = process.WaitForExit(effectiveTimeout * 1000);

                stopwatch.Stop();

                if (!completedNormally)
                {
                    // Kill on timeout
                    try { process.Kill(entireProcessTree: true); } catch { }
                    // Cancel async reads to flush any remaining buffered data
                    try { process.CancelOutputRead(); } catch { }
                    try { process.CancelErrorRead(); } catch { }
                    process.WaitForExit(3000); // Ensure process is fully dead
                    _sandboxService.ReleaseJobHandle(process.Id);
                    _sandboxService.CleanupSandbox(sandboxDir);

                    return new ExecutionResult
                    {
                        Status = "error",
                        Output = outputBuilder.ToString(),
                        Error = $"Command timed out after {effectiveTimeout} seconds and was terminated.\n建议：检查脚本是否存在死循环，或通过 params.timeout_sec 参数延长超时时间",
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        RiskLevel = riskResult.Level.ToString().ToLower(),
                        MatchedRules = riskResult.MatchedRules,
                        FullCommand = command
                    };
                }

                // Ensure all async output has been delivered to the event handlers
                process.CancelOutputRead();
                process.CancelErrorRead();
                _sandboxService.ReleaseJobHandle(process.Id);

                var status = process.ExitCode == 0 ? "success" : "error";
                var errorMsg = errorBuilder.ToString();
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(errorMsg))
                {
                    errorMsg = $"Process exited with code {process.ExitCode}";
                }

                return new ExecutionResult
                {
                    Status = status,
                    Output = outputBuilder.ToString(),
                    Error = errorMsg,
                    ExitCode = process.ExitCode,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    RiskLevel = riskResult.Level.ToString().ToLower(),
                    MatchedRules = riskResult.MatchedRules,
                    FullCommand = command
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ExecutionResult
                {
                    Status = "error",
                    Output = "",
                    Error = $"Execution failed: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    RiskLevel = riskResult.Level.ToString().ToLower(),
                    MatchedRules = riskResult.MatchedRules,
                    FullCommand = command
                };
            }
            finally
            {
                _sandboxService.CleanupSandbox(sandboxDir);
            }
        }

        // Show a WPF confirmation dialog that auto-rejects after 30 seconds.
        // Uses a custom Window instead of MessageBox.Show because MessageBox blocks
        // the Dispatcher and cannot be programmatically closed.
        private Task<bool> ShowConfirmationDialog(string command, RiskAssessmentResult risk)
        {
            var tcs = new TaskCompletionSource<bool>();

            Application.Current.Dispatcher.Invoke(() =>
            {
                var riskLabel = risk.Level switch
                {
                    RiskLevel.High => "高风险 (HIGH)",
                    RiskLevel.Medium => "中风险 (MEDIUM)",
                    _ => ""
                };

                var rulesText = string.Join("\n", risk.MatchedRules.Select(r => $"  - {r}"));

                var message = $"风险等级: {riskLabel}\n\n" +
                              $"检测到以下风险规则:\n{rulesText}\n\n" +
                              $"命令内容:\n{command}\n\n" +
                              $"说明: {risk.Description}\n\n" +
                              $"是否确认执行此命令？\n\n" +
                              $"（30秒后自动拒绝）";

                var window = new Window
                {
                    Title = "命令执行确认 - MoonYa",
                    Width = 500,
                    Height = 450,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ResizeMode = ResizeMode.CanResize,
                };

                var panel = new StackPanel { Margin = new Thickness(15) };
                var msgBlock = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 300,
                };
                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 15, 0, 0),
                };
                var yesBtn = new Button { Content = "确认执行", Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(5) };
                var noBtn = new Button { Content = "拒绝", Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(5) };
                btnPanel.Children.Add(yesBtn);
                btnPanel.Children.Add(noBtn);
                panel.Children.Add(msgBlock);
                panel.Children.Add(btnPanel);
                window.Content = panel;

                yesBtn.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
                noBtn.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
                window.Closed += (_, _) => { tcs.TrySetResult(false); };

                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(30)
                };
                timer.Tick += (_, _) => { timer.Stop(); tcs.TrySetResult(false); window.Close(); };
                timer.Start();

                window.ShowDialog();
            });

            return tcs.Task;
        }

        // Basic command syntax validation
        private static string? ValidateCommandSyntax(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Command is empty.";

            // Check for balanced double quotes
            int dqCount = command.Count(c => c == '"');
            if (dqCount % 2 != 0)
                return "Unbalanced double quotes (\").";

            // Check for balanced single quotes (only if it looks like PowerShell)
            if (command.Contains("$") || command.Contains("-Command"))
            {
                int sqCount = command.Count(c => c == '\'');
                if (sqCount % 2 != 0)
                    return "Unbalanced single quotes (').";
            }

            return null; // no syntax error detected
        }

        // Quick cleanup：清理沙箱目录并终止所有未退出的后台命令进程
        public void Shutdown()
        {
            _sandboxService.CleanupAll();

            // 终止所有后台进程并回收资源
            lock (_bgLock)
            {
                foreach (var bgCmd in _backgroundCommands.Values)
                {
                    try
                    {
                        if (bgCmd.Process != null && !bgCmd.Process.HasExited)
                        {
                            bgCmd.Process.Kill(entireProcessTree: true);
                        }
                        bgCmd.CleanupTimer?.Dispose();
                        try { bgCmd.Process?.Dispose(); } catch { }
                    }
                    catch { }
                }
                _backgroundCommands.Clear();
            }
        }
    }
}
