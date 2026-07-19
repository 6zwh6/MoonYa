// ┌─────────────────────────────────────────────────────────┐
// │  FileOperationApiServer — HTTP API for file operations   │
// │  PHP calls this via HTTP to execute file system actions  │
// └─────────────────────────────────────────────────────────┘

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MoonYa.Services
{
    public class FileOperationApiServer
    {
        private readonly FileOperationService _fileService;
        private readonly ComputerUseService _cuService;
        private readonly UiAutomationService _uiaService;
        private readonly LspServiceManager? _lspManager;
        private readonly int _port;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;

        // 环境感知相关服务（在构造函数中 new，避免修改 App.xaml.cs 启动代码）
        private readonly SystemStatusService _systemStatusService;
        private readonly AppInstallService _appInstallService;
        // edit_file 工具服务（view/str_replace/insert 三合一，复用 _fileService 的路径解析与安全校验）
        private readonly EditFileService _editFileService;
        // 代码搜索工具服务（grep/glob/view_directory，仿 Trae Agent 代码搜索工具集）
        private readonly GrepService _grepService;
        private readonly GlobService _globService;
        private readonly ViewDirectoryService _viewDirectoryService;
        // 命令执行服务：用于 execute_command / get_command_status / stop_command 三个 action
        // 在构造函数中 new，避免修改 App.xaml.cs 启动代码
        private readonly CommandExecutionService _cmdService;

        public int Port => _port;
        public bool IsRunning => _listener?.IsListening ?? false;

        public FileOperationApiServer(FileOperationService fileService, ComputerUseService cuService, UiAutomationService uiaService, int port = 58900)
            : this(fileService, cuService, uiaService, lspManager: null, port)
        {
        }

        public FileOperationApiServer(FileOperationService fileService, ComputerUseService cuService, UiAutomationService uiaService, LspServiceManager? lspManager, int port = 58900)
        {
            _fileService = fileService;
            _cuService = cuService;
            _uiaService = uiaService;
            _lspManager = lspManager;
            _port = port;
            _systemStatusService = new SystemStatusService();
            _appInstallService = new AppInstallService();
            _editFileService = new EditFileService(fileService);
            _grepService = new GrepService();
            _globService = new GlobService();
            _viewDirectoryService = new ViewDirectoryService();

            // 创建命令执行服务实例（用于 execute_command / get_command_status / stop_command）
            var riskService = new RiskAssessmentService();
            var sandboxService = new SandboxService();
            _cmdService = new CommandExecutionService(riskService, sandboxService, 60);
        }

        public void Start()
        {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                System.Diagnostics.Debug.WriteLine($"FileOperationApiServer: Failed to start on port {_port}: {ex.Message}");
                return;
            }

            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            System.Diagnostics.Debug.WriteLine($"FileOperationApiServer: Started on http://127.0.0.1:{_port}/");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            // 终止所有未退出的后台命令进程并清理资源
            try { _cmdService?.Shutdown(); } catch { }
            System.Diagnostics.Debug.WriteLine("FileOperationApiServer: Stopped.");
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var contextTask = _listener.GetContextAsync();
                    var completedTask = await Task.WhenAny(contextTask, Task.Delay(-1, ct));
                    
                    if (ct.IsCancellationRequested) break;
                    
                    var context = await contextTask;
                    _ = Task.Run(() => HandleRequest(context), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FileOperationApiServer: {ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // CORS preflight：浏览器跨域请求前的 OPTIONS 预检，直接 204 返回
                if (request.HttpMethod == "OPTIONS")
                {
                    SetCorsHeaders(response);
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                // 所有响应都附加 CORS 头（与 ExecutionApiServer.cs 一致）
                SetCorsHeaders(response);

                // Security: only localhost
                if (!request.RemoteEndPoint!.Address.Equals(IPAddress.Loopback) &&
                    !request.RemoteEndPoint!.Address.Equals(IPAddress.IPv6Loopback))
                {
                    await Respond(response, 403, Json(new { success = false, message = "只允许本地访问" }));
                    return;
                }

                // Only POST /file-op or /cu-op, GET /cu/window-snapshot
                var path = request.Url!.AbsolutePath;
                bool isFileOp = path.Equals("/file-op", StringComparison.OrdinalIgnoreCase);
                bool isCuOp = path.Equals("/cu-op", StringComparison.OrdinalIgnoreCase);
                bool isWindowSnapshot = path.Equals("/cu/window-snapshot", StringComparison.OrdinalIgnoreCase);

                // GET/POST /cu/window-snapshot：返回顶层窗口清单（按 Z-order）
                // PHP 端通过 SSE 中继发送请求，中继始终使用 POST；若页面直接 JS fetch 可用 GET。
                if (isWindowSnapshot)
                {
                    if (request.HttpMethod != "GET" && request.HttpMethod != "POST")
                    {
                        await Respond(response, 405, Json(new { success = false, message = "Method Not Allowed" }));
                        return;
                    }
                    await HandleWindowSnapshot(response);
                    return;
                }

                if (request.HttpMethod != "POST" || (!isFileOp && !isCuOp))
                {
                    await Respond(response, 404, Json(new { success = false, message = "Not Found" }));
                    return;
                }

                // Read body
                string body;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync();
                }

                // Route to /cu-op handler (ComputerUseService)
                if (isCuOp)
                {
                    await HandleCuOp(response, body);
                    return;
                }

                var req = JsonSerializer.Deserialize<FileOpRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (req == null || string.IsNullOrWhiteSpace(req.Action))
                {
                    await Respond(response, 400, Json(new { success = false, message = "缺少 action 参数" }));
                    return;
                }

                // Route to FileOperationService
                object result = req.Action switch
                {
                    "create_file" => await _fileService.CreateFile(req.Path ?? "", req.Content),
                    "create_folder" => await _fileService.CreateFolder(req.Path ?? ""),
                    "delete_file" => await _fileService.DeleteFile(req.Path ?? ""),
                    "open_file" => await _fileService.OpenFile(req.Path ?? ""),
                    // read_file：透传 offset/limit 参数，返回内容带行号（cat -n 格式）
                    "read_file" => await _fileService.ReadFile(req.Path ?? "", req.Offset, req.Limit),
                    "open_app" => await _fileService.OpenApp(req.Path ?? ""),
                    "close_app" => await _fileService.CloseApp(req.Path ?? ""),
                    "uninstall_app" => await _fileService.UninstallApp(req.Path ?? ""),
                    "download_file" => await _fileService.Download(
                        req.DownloadUrl ?? "",
                        ResolveDownloadPath(req.DownloadUrl ?? "", req.DownloadPath ?? ""),
                        null),
                    "copy_file" => await _fileService.CopyFile(req.Path ?? "", req.Destination ?? ""),
                    "move_file" => await _fileService.MoveFile(req.Path ?? "", req.Destination ?? ""),
                    "list_files" => await _fileService.ListFiles(req.Path ?? ""),
                    // 项目文件夹管理路由
                    "pick_folder" => await _fileService.PickFolder(),
                    "validate_path" => await _fileService.ValidatePath(req.Path ?? ""),
                    "create_project_folder" => await _fileService.CreateProjectFolder(req.Path ?? ""),
                    // 环境感知相关路由
                    "get_system_status" => await _systemStatusService.GetSystemStatusAsync(),
                    "check_app_installed" => await _appInstallService.CheckInstalled(req.AppName ?? ""),
                    "install_app" => await _appInstallService.InstallApp(req.AppName ?? ""),
                    // edit_file 工具（view/str_replace/insert 三合一，仿 Trae Agent TextEditorTool）
                    "edit_file" => await _editFileService.HandleEditFile(req),
                    // grep 工具：基于 ripgrep 的内容搜索（正则/文件类型/上下文行）
                    "grep" => await _grepService.SearchAsync(req.ToGrepParams()),
                    // glob 工具：文件名 glob 模式匹配（**/*.php），按修改时间倒序返回
                    "glob" => await _globService.SearchAsync(req.Pattern ?? "", req.Path),
                    // view_directory 工具：目录树查看（深度可配置，默认 2 层）
                    "view_directory" => await _viewDirectoryService.ViewAsync(req.Path ?? "", req.Depth ?? 2, req.ExcludePatterns),
                    // execute_command：透传 blocking/cwd/timeout 参数
                    // blocking=null/true → 同步执行返回 ExecutionResult；blocking=false → 后台启动返回 command_id
                    "execute_command" => await _cmdService.ExecuteAsync(
                        req.Command ?? "",
                        true,               // autoApproveMediumRisk
                        req.Cwd,
                        req.Timeout,
                        req.Blocking),
                    // get_command_status：查询后台命令状态（running/done/killed + exit_code + output）
                    "get_command_status" => await _cmdService.GetCommandStatusAsync(req.CommandId ?? ""),
                    // stop_command：终止后台命令（Process.Kill(entireProcessTree: true)）
                    "stop_command" => await _cmdService.StopCommandAsync(req.CommandId ?? ""),
                    // LSP 工具：诊断 / 引用查找 / 定义跳转
                    // 委托给 LspServiceManager：自动检测语言、推断工作区、按需启动 LSP
                    // 注：_lspManager 为 null 时返回错误（未启用 LSP 集成）
                    "get_diagnostics" => _lspManager != null
                        ? await _lspManager.GetDiagnosticsAsync(req.Path ?? "")
                        : new { success = false, error = "LSP 服务未启用" },
                    "find_references" => _lspManager != null
                        ? await _lspManager.FindReferencesAsync(req.Path ?? "", req.Line ?? 0, req.Column ?? 0)
                        : new { success = false, error = "LSP 服务未启用" },
                    "goto_definition" => _lspManager != null
                        ? await _lspManager.GotoDefinitionAsync(req.Path ?? "", req.Line ?? 0, req.Column ?? 0)
                        : new { success = false, error = "LSP 服务未启用" },
                    _ => new { success = false, message = $"未知操作: {req.Action}" }
                };

                await Respond(response, 200, Json(result));
            }
            catch (Exception ex)
            {
                await Respond(response, 500, Json(new { success = false, message = $"内部错误: {ex.Message}" }));
            }
        }

        /// <summary>
        /// 处理 /cu-op 路由请求：截屏 / 鼠标 / 键盘等电脑操作。
        /// 请求体格式与 /file-op 一致（JSON，含 action 字段），具体字段按 action 不同。
        /// </summary>
        private async Task HandleCuOp(HttpListenerResponse response, string body)
        {
            try
            {
                var req = JsonSerializer.Deserialize<CuOpRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (req == null || string.IsNullOrWhiteSpace(req.Action))
                {
                    await Respond(response, 400, Json(new { success = false, message = "缺少 action 参数" }));
                    return;
                }

                object result = req.Action switch
                {
                    "take_screenshot" => _cuService.TakeScreenshot(req.Target),
                    "get_cursor_pos" => _cuService.GetCursorPosition(),
                    "mouse_move" => _cuService.MouseMove(req.X ?? 0, req.Y ?? 0, req.ScaleRatio ?? 1.0),
                    "mouse_click" => _cuService.MouseClick(
                        req.X ?? 0, req.Y ?? 0, req.Button ?? "left", req.Click ?? "single", req.ScaleRatio ?? 1.0),
                    "mouse_scroll" => _cuService.MouseScroll(req.Delta ?? 0),
                    "mouse_drag" => _cuService.MouseDrag(
                        req.FromX ?? 0, req.FromY ?? 0, req.ToX ?? 0, req.ToY ?? 0,
                        req.Button ?? "left", req.Points, req.ScaleRatio ?? 1.0),
                    "mouse_hold" => _cuService.MouseHold(
                        req.X ?? 0, req.Y ?? 0,
                        req.Button ?? "left",
                        req.Duration ?? 500),
                    "keyboard_type" => _cuService.KeyboardType(req.Text ?? ""),
                    "key_press" => _cuService.KeyPress(req.Keys ?? ""),
                    // === UIA 操作（UiAutomationService）===
                    // find_element: 通过 AutomationId / Name / ControlType 在 parentElementId 子树中查找元素
                    "find_element" => _uiaService.FindElement(
                        req.ParentElementId, req.AutomationId, req.Name, req.ControlType),
                    // get_ui_tree: 获取 parentElementId 子树的 UI 元素树（maxDepth 默认 6）
                    // 注：复用 ParentElementId 字段作为树根，不再单独定义 RootElementId
                    // format: "text"（qoder 风格缩进文本，默认）或 "json"（v1 JSON 结构）
                    // maxNodes: 节点数上限，默认由 UiAutomationService 兜底（2000）
                    "get_ui_tree" => _uiaService.GetUiTree(
                        req.ParentElementId,
                        req.MaxDepth ?? 6,
                        req.Format ?? "text",
                        req.MaxNodes ?? 0),
                    // click_element: 点击指定 elementId 对应的 UI 元素
                    "click_element" => _uiaService.ClickElement(req.ElementId ?? ""),
                    // set_text: 向指定 elementId 元素输入文本
                    "set_text" => _uiaService.SetText(req.ElementId ?? "", req.Text ?? ""),
                    // get_text: 读取指定 elementId 元素的文本
                    "get_text" => _uiaService.GetText(req.ElementId ?? ""),
                    // focus_window: 通过 HWND 精确激活窗口（Alt+Tab 不可靠，需直接 SetForegroundWindow）
                    "focus_window" => _cuService.FocusWindow(req.Hwnd ?? 0),
                    // capture_ui_snapshot: 一次合成 UIA 树 + 截图 + 焦点元素 + 窗口元信息（Task 11）
                    // include_screenshot=false 时 screenshots 为空数组
                    "capture_ui_snapshot" => _uiaService.CaptureUiSnapshot(
                        req.MaxDepth ?? 6,
                        req.IncludeScreenshot ?? true,
                        req.ScreenshotTarget ?? "window"),
                    _ => new { success = false, message = $"未知操作: {req.Action}" }
                };

                await Respond(response, 200, Json(result));
            }
            catch (Exception ex)
            {
                await Respond(response, 500, Json(new { success = false, message = $"内部错误: {ex.Message}" }));
            }
        }

        /// <summary>
        /// 处理 GET /cu/window-snapshot：返回顶层窗口清单（按 Z-order 顶到底）。
        /// </summary>
        private async Task HandleWindowSnapshot(HttpListenerResponse response)
        {
            try
            {
                if (_uiaService == null)
                {
                    await Respond(response, 503, Json(new { error = "UIA service unavailable" }));
                    return;
                }

                var windows = _uiaService.GetWindowSnapshot();
                var list = new List<object>(windows.Count);
                foreach (var w in windows)
                {
                    list.Add(new
                    {
                        title = w.Title,
                        process_name = w.ProcessName,
                        pid = w.ProcessId,
                        hwnd = w.Hwnd.ToString(),
                        is_visible = w.IsVisible
                    });
                }

                await Respond(response, 200, Json(new { windows = list }));
            }
            catch (Exception ex)
            {
                await Respond(response, 500, Json(new { success = false, message = $"内部错误: {ex.Message}" }));
            }
        }

        private static string ResolveDownloadPath(string url, string dlPath)
        {
            if (string.IsNullOrWhiteSpace(dlPath))
            {
                dlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "download");
            }

            if (Directory.Exists(dlPath) || dlPath.EndsWith("\\") || dlPath.EndsWith("/") || !Path.HasExtension(dlPath))
            {
                var fileName = "download";
                if (!string.IsNullOrWhiteSpace(url))
                {
                    try
                    {
                        var uri = new Uri(url);
                        var seg = uri.Segments.LastOrDefault();
                        if (!string.IsNullOrWhiteSpace(seg) && seg != "/")
                        {
                            fileName = Uri.UnescapeDataString(seg);
                        }
                    }
                    catch { }
                }
                dlPath = Path.Combine(dlPath, fileName);
            }

            return dlPath;
        }

        private static async Task Respond(HttpListenerResponse response, int statusCode, string body)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        // CORS 头：与 ExecutionApiServer.SetCorsHeaders 保持一致
        private static void SetCorsHeaders(HttpListenerResponse response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");
            // Private Network Access：允许 HTTPS 页面跨域请求本地 HTTP API（Chrome 94+ 要求）
            response.Headers.Add("Access-Control-Allow-Private-Network", "true");
        }

        private static string Json(object obj) =>
            JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public class FileOpRequest
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }
        [JsonPropertyName("path")]
        public string? Path { get; set; }
        [JsonPropertyName("content")]
        public string? Content { get; set; }
        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }
        [JsonPropertyName("download_path")]
        public string? DownloadPath { get; set; }
        [JsonPropertyName("destination")]
        public string? Destination { get; set; }
        [JsonPropertyName("app_name")]
        public string? AppName { get; set; }

        // === edit_file 工具相关字段（view/str_replace/insert 三合一）===
        /// <summary>子命令：view / str_replace / insert</summary>
        [JsonPropertyName("command")]
        public string? Command { get; set; }
        /// <summary>str_replace 的待替换文本</summary>
        [JsonPropertyName("old_str")]
        public string? OldStr { get; set; }
        /// <summary>str_replace 替换后的文本 / insert 待插入的文本</summary>
        [JsonPropertyName("new_str")]
        public string? NewStr { get; set; }
        /// <summary>insert 命令的插入行号（0=文件开头，N=第 N 行后）</summary>
        [JsonPropertyName("insert_line")]
        public int? InsertLine { get; set; }
        /// <summary>view 命令的行号范围 [start, end]（end=-1 表示到文件末尾）；目录模式下 viewRange[1] 为最大深度</summary>
        [JsonPropertyName("view_range")]
        public int[]? ViewRange { get; set; }
        /// <summary>工作目录（相对路径基准），支持 AI 传项目根目录让相对路径解析到正确位置</summary>
        [JsonPropertyName("cwd")]
        public string? Cwd { get; set; }

        // === grep 工具相关字段（基于 ripgrep 的内容搜索）===
        /// <summary>正则表达式（必填）</summary>
        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }
        /// <summary>输出模式：content / files_with_matches / count</summary>
        [JsonPropertyName("output_mode")]
        public string? OutputMode { get; set; }
        /// <summary>上下文行数（-B，匹配行之前）</summary>
        [JsonPropertyName("context_before")]
        public int? ContextBefore { get; set; }
        /// <summary>上下文行数（-A，匹配行之后）</summary>
        [JsonPropertyName("context_after")]
        public int? ContextAfter { get; set; }
        /// <summary>上下文行数（-C，同时设置 -A 和 -B）</summary>
        [JsonPropertyName("context")]
        public int? Context { get; set; }
        /// <summary>是否显示行号（-n，默认 true）</summary>
        [JsonPropertyName("show_line_numbers")]
        public bool? ShowLineNumbers { get; set; }
        /// <summary>是否大小写不敏感（-i，默认 false）</summary>
        [JsonPropertyName("case_insensitive")]
        public bool? CaseInsensitive { get; set; }
        /// <summary>文件名 glob 过滤（--glob，如 *.php）</summary>
        [JsonPropertyName("glob_filter")]
        public string? GlobFilter { get; set; }
        /// <summary>文件类型过滤（--type，如 php/py/js）</summary>
        [JsonPropertyName("type_filter")]
        public string? TypeFilter { get; set; }

        // === view_directory 工具相关字段 ===
        /// <summary>目录树递归深度（默认 2 层）</summary>
        [JsonPropertyName("depth")]
        public int? Depth { get; set; }
        /// <summary>自定义排除目录模式列表（与默认排除合并）</summary>
        [JsonPropertyName("exclude_patterns")]
        public List<string>? ExcludePatterns { get; set; }

        // === read_file 工具相关字段（带行号 + 分段读取）===
        /// <summary>起始行号（1-based，默认 1）</summary>
        [JsonPropertyName("offset")]
        public int? Offset { get; set; }
        /// <summary>读取行数（默认全部；大文件自动分段）</summary>
        [JsonPropertyName("limit")]
        public int? Limit { get; set; }

        // === execute_command / get_command_status / stop_command 工具相关字段 ===
        // 注：Command 字段在 edit_file 中表示子命令（view/str_replace/insert），
        //     在 execute_command 中表示 shell 命令字符串。两种用途不会同时出现。
        /// <summary>execute_command 阻塞模式：true=同步等待结果，false=后台运行返回 command_id，null 默认 true</summary>
        [JsonPropertyName("blocking")]
        public bool? Blocking { get; set; }
        /// <summary>execute_command 同步模式超时秒数（后台模式忽略）</summary>
        [JsonPropertyName("timeout")]
        public int? Timeout { get; set; }
        /// <summary>get_command_status / stop_command 的目标 command_id</summary>
        [JsonPropertyName("command_id")]
        public string? CommandId { get; set; }

        // === LSP 工具相关字段（find_references / goto_definition）===
        // 注：LSP position 使用 0-based 行列（line 0=第一行，column 0=第一个字符）
        /// <summary>符号位置：行号（0-based）</summary>
        [JsonPropertyName("line")]
        public int? Line { get; set; }
        /// <summary>符号位置：列号（0-based）</summary>
        [JsonPropertyName("column")]
        public int? Column { get; set; }

        /// <summary>
        /// 将 FileOpRequest 中 grep 相关字段转换为 GrepParams 对象。
        /// 处理 null → 默认值的映射逻辑。
        /// </summary>
        public GrepParams ToGrepParams() => new GrepParams
        {
            Pattern = Pattern ?? "",
            Path = string.IsNullOrEmpty(Path) ? "." : Path,
            OutputMode = string.IsNullOrEmpty(OutputMode) ? "content" : OutputMode,
            ContextBefore = ContextBefore,
            ContextAfter = ContextAfter,
            Context = Context,
            ShowLineNumbers = ShowLineNumbers ?? true,
            CaseInsensitive = CaseInsensitive ?? false,
            GlobFilter = GlobFilter,
            TypeFilter = TypeFilter
        };
    }

    /// <summary>
    /// /cu-op 路由请求体：电脑操作（截屏 / 鼠标 / 键盘）。
    /// 所有字段按 action 类型不同使用，未使用字段保持 null。
    /// </summary>
    public class CuOpRequest
    {
        public string? Action { get; set; }
        // take_screenshot: 截图目标 "window"（默认）/ "screen"
        // 未传时 ComputerUseService 内部默认为 "window"
        public string? Target { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        // mouse_drag: 起点和终点坐标（PHP 端用 snake_case，需 JsonPropertyName 映射）
        [JsonPropertyName("from_x")]
        public int? FromX { get; set; }
        [JsonPropertyName("from_y")]
        public int? FromY { get; set; }
        [JsonPropertyName("to_x")]
        public int? ToX { get; set; }
        [JsonPropertyName("to_y")]
        public int? ToY { get; set; }
        // mouse_drag: 路径点数组（曲线模式），非空时忽略 from/to，按顺序经过每个点
        public List<DragPoint>? Points { get; set; }
        public string? Button { get; set; }
        public string? Click { get; set; }
        public int? Delta { get; set; }
        // mouse_hold: 按住时长（毫秒）
        public int? Duration { get; set; }
        public string? Text { get; set; }
        public string? Keys { get; set; }
        // mouse_move/mouse_click/mouse_drag: 截图缩放比，用于将 AI 返回坐标还原为物理坐标
        [JsonPropertyName("scale_ratio")]
        public double? ScaleRatio { get; set; }
        // focus_window: 目标窗口 HWND（从 window-snapshot 获取）
        public long? Hwnd { get; set; }
        // === UIA 字段（UiAutomationService）===
        // find_element: 父元素 ID（限定搜索范围）；get_ui_tree: 复用为树根 ID
        public string? ParentElementId { get; set; }
        // click_element / set_text / get_text: 目标元素 ID
        public string? ElementId { get; set; }
        // get_ui_tree: 树最大深度，默认 6
        public int? MaxDepth { get; set; }
        // get_ui_tree: 序列化格式 "text"（qoder 风格，默认）或 "json"（v1 JSON 结构）
        public string? Format { get; set; }
        // get_ui_tree: 节点数上限，0 或 null 使用 UiAutomationService 兜底默认值（2000）
        public int? MaxNodes { get; set; }
        // find_element: 元素 AutomationId 条件
        public string? AutomationId { get; set; }
        // find_element: 元素 Name 条件
        public string? Name { get; set; }
        // find_element: 元素 ControlType 条件（如 Button / Edit / ListItem）
        public string? ControlType { get; set; }
        // === capture_ui_snapshot 字段（Task 11）===
        // capture_ui_snapshot: 是否包含截图，默认 true；false 时 screenshots 为空数组
        public bool? IncludeScreenshot { get; set; }
        // capture_ui_snapshot: 截图目标 "window"（默认）或 "screen"
        public string? ScreenshotTarget { get; set; }
    }

    /// <summary>
    /// mouse_drag 路径点（曲线模式用）
    /// </summary>
    public class DragPoint
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
