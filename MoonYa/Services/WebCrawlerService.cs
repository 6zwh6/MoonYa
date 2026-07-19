using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace MoonYa.Services
{
    /// <summary>
    /// 管理 MoonYa-Python/main.py 进程（统一爬虫+搜索后端）
    /// </summary>
    public class WebCrawlerService
    {
        private Process? _process;
        private readonly string _scriptPath;
        private readonly int _port;
        private readonly string _logPath;

        public int Port => _port;
        public bool IsRunning => _process != null && !_process.HasExited;

        public WebCrawlerService(int port = 58901)
        {
            _port = port;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _scriptPath = FindScript(baseDir);
            _logPath = Path.Combine(baseDir, "python_backend.log");
        }

        /// <summary>
        /// 写入日志文件（带时间戳）
        /// </summary>
        private void Log(string message, string level = "INFO")
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }

        private static string FindScript(string baseDir)
        {
            // Try several locations for main.py (unified Python backend)
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "MoonYa-Python", "main.py")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "MoonYa-Python", "main.py")),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            return candidates[0];
        }

        public void Start()
        {
            if (IsRunning) return;

            Log("启动 Python 后端服务...");

            if (!File.Exists(_scriptPath))
            {
                Log($"SCRIPT_NOT_FOUND: {_scriptPath}", "ERROR");
                return;
            }

            var pythonExe = FindPython();
            if (pythonExe == null)
            {
                Log("PYTHON_NOT_FOUND: 找不到 python.exe", "ERROR");
                return;
            }

            Log($"Python: {pythonExe}");
            Log($"Script: {_scriptPath}");

            // 传递给 main.py 的 config.json 路径
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");
            if (!File.Exists(configPath))
                configPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "launcher_config.json"));
            var args = File.Exists(configPath) ? $"\"{_scriptPath}\" \"{configPath}\"" : $"\"{_scriptPath}\"";

            Log($"Args: {args}");

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _process = new Process { StartInfo = startInfo };
            _process.EnableRaisingEvents = true;

            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    System.Diagnostics.Debug.WriteLine($"[python] {e.Data}");
                    Log(e.Data, "PYTHON");
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    System.Diagnostics.Debug.WriteLine($"[python-err] {e.Data}");
                    Log(e.Data, "PYTHON_ERR");
                }
            };

            _process.Exited += (_, _) =>
            {
                Log($"进程退出，退出码: {_process.ExitCode}", _process.ExitCode == 0 ? "INFO" : "ERROR");
            };

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                Log($"Python 后端服务已启动 (PID: {_process.Id}, port: {_port})");
            }
            catch (Exception ex)
            {
                Log($"启动失败: {ex.Message}", "ERROR");
                _process = null;
            }
        }

        public void Stop()
        {
            if (_process == null) return;

            Log("正在停止 Python 后端服务...");
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch { }
            finally
            {
                _process.Dispose();
                _process = null;
            }

            Log("Python 后端服务已停止");
        }

        private static string? FindPython()
        {
            // Try the project's .venv Python first
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] venvCandidates =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "MoonYa-Python", ".venv", "Scripts", "python.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "MoonYa-Python", ".venv", "Scripts", "python.exe")),
            };
            foreach (var vc in venvCandidates)
            {
                if (File.Exists(vc)) return vc;
            }

            // Prefer python3, then python
            string[] names = { "python3", "python" };

            foreach (var name in names)
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(startInfo);
                    if (p != null)
                    {
                        p.WaitForExit(3000);
                        if (p.ExitCode == 0) return name;
                    }
                }
                catch { }
            }

            // Check common install paths
            string[] commonPaths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python313", "python.exe"),
                @"C:\Python311\python.exe",
                @"C:\Python312\python.exe",
                @"C:\Python313\python.exe",
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path)) return path;
            }

            return null;
        }
    }
}
