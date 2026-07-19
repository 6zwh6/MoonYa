using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using MoonYa.Services;

namespace MoonYa
{
    public partial class App : Application
    {
        private static FileOperationApiServer? _apiServer;
        private static ExecutionApiServer? _executionServer;
        private static WebCrawlerService? _crawlerService;
        private static ComputerUseService? _cuService;
        // 浏览器自动化服务（PuppeteerSharp 引擎 + HTTP API 监听 58905）
        private static BrowserAutomationService? _browserService;
        private static BrowserApiServer? _browserApiServer;
        // LSP 服务管理：管理 PHP/Python/JS-TS 三种语言的 LSP 进程
        // LspApiServer 独立监听 58906（避免与 FileOpApiServer 58900 / WebCrawler 58901 /
        // ExecutionApiServer 58903 / BrowserApiServer 58905 冲突）
        private static LspServiceManager? _lspManager;
        private static LspApiServer? _lspApiServer;
        private static System.Windows.Forms.NotifyIcon? _notifyIcon;
        internal static System.Windows.Forms.NotifyIcon? TrayIcon => _notifyIcon;

        // ── 桌宠桌面覆盖窗口（透明置顶，独立于主窗口）──
        internal static PetWindow? PetWindow { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ── 设置爬虫数据目录环境变量（PHP config.php 读取）──
            Environment.SetEnvironmentVariable("MOONYA_CRAWLER_DATA_DIR", AppDomain.CurrentDomain.BaseDirectory);

            // ── Read launcher config for API port ─────────
            int apiPort = 58900;
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");
            if (!File.Exists(configPath))
                configPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "launcher_config.json"));
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<JsonElement>(json);
                    if (config.TryGetProperty("api_port", out var portProp) && portProp.TryGetInt32(out var p))
                        apiPort = p;
                }
                catch { }
            }

            // ── Start file operation HTTP API ─────────────
            var fileService = new FileOperationService();
            _cuService = new ComputerUseService();
            // UiAutomationService 作为单例：依赖 cuService 提供 UIA 失败时的鼠标/键盘 fallback
            var uiaService = new UiAutomationService(_cuService);
            // LSP 服务管理器：构造时注入到 FileOperationApiServer，使其能直接分发 get_diagnostics 等 action
            _lspManager = new LspServiceManager();
            _apiServer = new FileOperationApiServer(fileService, _cuService, uiaService, _lspManager, apiPort);
            _apiServer.Start();

            // ── Start LSP API server (独立端口 58906) ──────
            // PHP/Python/JS-TS 三种语言的 LSP 服务管理
            // 端口冲突说明：规范要求 58901，但 58901 已被 WebCrawlerService 占用，故顺延使用 58906
            try
            {
                _lspApiServer = new LspApiServer(_lspManager, 58906);
                _lspApiServer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LspApiServer 启动失败: {ex.Message}");
            }

            // ── Start Python unified backend service (main.py: crawl + search) ──
            int pythonPort = 58901;
            if (File.Exists(configPath))
            {
                try
                {
                    var configJson = File.ReadAllText(configPath);
                    var cfg = JsonSerializer.Deserialize<JsonElement>(configJson);
                    if (cfg.TryGetProperty("crawler_port", out var cp) && cp.TryGetInt32(out var cpv))
                        pythonPort = cpv;
                }
                catch { }
            }
            _crawlerService = new WebCrawlerService(pythonPort);
            _crawlerService.Start();

            // ── Start Execution API server (Python + CLI execution tools) ──
            int executionPort = 58903;
            if (File.Exists(configPath))
            {
                try
                {
                    var execJson = File.ReadAllText(configPath);
                    var execCfg = JsonSerializer.Deserialize<JsonElement>(execJson);
                    if (execCfg.TryGetProperty("execution_port", out var ep) && ep.TryGetInt32(out var epv))
                        executionPort = epv;
                }
                catch { }
            }
            _executionServer = new ExecutionApiServer(executionPort);
            _executionServer.Start();

            // ── CefSharp settings ─────────────────────────
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MoonYa", "CefCache");

            // ── 定位 CEF 原生资源目录 ──
            // Debug 模式下位于 runtimes/{rid}/native/，自包含发布后可能展开到根目录
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            var nativeDir = Path.Combine(baseDir, "runtimes", rid, "native");
            if (!Directory.Exists(nativeDir))
                nativeDir = baseDir;   // 自包含发布：原生文件直接输出到根目录

            // 检查 BrowserSubprocess 是否存在，若 runtimes 目录不存在则使用根目录
            var subprocessPath = Path.Combine(nativeDir, "CefSharp.BrowserSubprocess.exe");
            if (!File.Exists(subprocessPath) && nativeDir != baseDir)
            {
                nativeDir = baseDir;
                subprocessPath = Path.Combine(baseDir, "CefSharp.BrowserSubprocess.exe");
            }

            // locales 目录可能在根目录或 locales 子目录
            var localesDir = Path.Combine(nativeDir, "locales");
            if (!Directory.Exists(localesDir))
                localesDir = nativeDir;

            var settings = new CefSettings
            {
                CachePath = cachePath,
                UserAgent = "MoonYaDesktop/1.0",
                LogSeverity = LogSeverity.Verbose,
                LogFile = Path.Combine(baseDir, "cef.log"),
                ResourcesDirPath = nativeDir,
                LocalesDirPath = localesDir,
                BrowserSubprocessPath = subprocessPath,
            };

            // ── GPU 加速优化（OSR 模式下提升光栅化与合成效率）──
            settings.CefCommandLineArgs.Add("enable-gpu-rasterization", "1");
            settings.CefCommandLineArgs.Add("ignore-gpu-blocklist", "1");

            // ── 允许 HTTPS 页面向本地 HTTP API 发起请求（Mixed Content + PNA）──
            settings.CefCommandLineArgs.Add("allow-running-insecure-content", "1");
            // ★ 禁用 Tracking Prevention（阻止 localStorage 等 Web API 访问）+ 私有网络请求限制
            settings.CefCommandLineArgs.Add("disable-features", "BlockInsecurePrivateNetworkRequests,BlockInsecurePrivateNetworkRequestsForOtherOrigin,TrackingProtection,PrivacySandboxAdsAPIS,PrivacySandboxCookies,PrivacySandboxFirstPartySets");

            // ── 自动授予麦克风和摄像头权限（无需弹窗授权）──
            settings.CefCommandLineArgs.Add("enable-media-stream", "1");
            // ★ 禁用站点隔离（避免 CefSharp OSR 模式下的渲染问题）
            settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");

            // ── 放宽自动播放策略：允许无需用户手势即可播放音频 ──
            //   根因：Ctrl+空格由 C# 全局键盘 Hook 捕获后通过 ExecuteScriptAsync 注入 JS，
            //   该调用不在 Chromium 的"用户手势"上下文中，导致首次创建的 AudioContext
            //   处于 suspended 状态且 resume() 无法激活，PTT 开始/结束提示音丢失。
            //   桌面应用的所有 JS 调用均由 C# 注入，不存在浏览器原生用户手势，
            //   因此必须放开自动播放策略才能让 AudioContext 首次即处于 running。
            settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

            // Ensure child processes exit when parent closes
            CefSharpSettings.SubprocessExitIfParentProcessClosed = true;

            // Enable Task-returning async methods in JS bridge
            CefSharpSettings.ConcurrentTaskExecution = true;

            // 标记 shutdown on exit 释放 CEF 资源
            CefSharpSettings.ShutdownOnExit = true;

            // ★ 清理残留的 CefSharp 子进程（避免上次崩溃后子进程未退出导致初始化失败）
            try
            {
                var currentPid = System.Environment.ProcessId;
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("CefSharp.BrowserSubprocess"))
                {
                    if (!proc.HasExited && proc.Id != currentPid)
                    {
                        proc.Kill();
                        proc.WaitForExit(2000);
                    }
                }
            }
            catch { }

            // ★ 启动时仅清理 CefSharp 的 HTTP/JS 缓存目录，保留 LocalStorage、Cookies、Session Storage
            //   等持久化数据，避免用户每次启动都需要重新登录。
            //   必须放在清理子进程之后，否则旧子进程可能仍占用缓存文件导致删除失败。
            try
            {
                if (Directory.Exists(cachePath))
                {
                    // 清理每个 Profile 下的 HTTP 缓存、JS 代码缓存、GPU 缓存即可；
                    // Local Storage / Session Storage / Cookies / Login Data 等保留。
                    var cacheDirsToClear = new[] { "Cache", "Code Cache", "GPUCache" };
                    foreach (var profileDir in Directory.GetDirectories(cachePath))
                    {
                        foreach (var dirName in cacheDirsToClear)
                        {
                            var dirPath = Path.Combine(profileDir, dirName);
                            if (!Directory.Exists(dirPath)) continue;
                            try
                            {
                                foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
                                {
                                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                                }
                                Directory.Delete(dirPath, recursive: true);
                                Debug.WriteLine($"[CefCache] 已清理缓存目录: {dirPath}");
                            }
                            catch (Exception dirEx)
                            {
                                Debug.WriteLine($"[CefCache] 清理目录失败 {dirPath}: {dirEx.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CefCache] 清理缓存失败（可能目录被占用）: {ex.Message}");
            }

            // Initialize CEF (must happen before any browser instance)
            var success = Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null);
            if (!success)
            {
                // 记录详细错误信息到日志文件
                var errorLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cef_error.log");
                try
                {
                    File.WriteAllText(errorLog,
                        $"CEF Initialize failed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                        $"BaseDir: {AppDomain.CurrentDomain.BaseDirectory}\r\n" +
                        $"CachePath: {cachePath}\r\n" +
                        $"NativeDir: {nativeDir}\r\n" +
                        $"LocalesDir: {localesDir}\r\n" +
                        $"SubprocessPath: {subprocessPath}\r\n" +
                        $"SubprocessExists: {File.Exists(subprocessPath)}\r\n" +
                        $"OS: {Environment.OSVersion}\r\n" +
                        $"Is64Bit: {Environment.Is64BitProcess}\r\n" +
                        $"CurrentDirectory: {Environment.CurrentDirectory}\r\n");
                }
                catch { }
                throw new InvalidOperationException("CefSharp initialization failed. 查看 cef.log 或 cef_error.log 了解详情。");
            }

            // ── Start Browser Automation API server (PuppeteerSharp-based browser control) ──
            // 监听 58905；启动失败仅记录日志，不阻塞主程序启动
            try
            {
                var baConfig = LoadBrowserAutomationConfig(configPath);
                _browserService = new BrowserAutomationService(baConfig);
                var securityGate = new BrowserSecurityGate();
                _browserApiServer = new BrowserApiServer(_browserService, securityGate, baConfig.Port);
                // 后台启动监听，不阻塞 UI；StartAsync 内部已用 Task.Run 包裹监听循环
                _ = _browserApiServer.StartAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BrowserApiServer 启动失败: {ex.Message}");
            }

            // ── System tray icon ──────────────────────────
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yax_rounded.ico");
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = File.Exists(iconPath) ? new Icon(iconPath) : System.Drawing.SystemIcons.Application,
                Text = "MoonYa",
                Visible = false
            };

            // ── 创建桌宠窗口（桌面透明覆盖，独立于主窗口）──
            //   根据 %AppData%\MoonYa\pet_settings.json 中的 enabled 字段决定是否默认显示。
            //   后续由 user_xinxi.php 中的开关通过 CefSharp JS 桥 petController.setEnabled() 控制。
            try
            {
                PetWindow = new PetWindow();
                if (LoadPetEnabledFromSettings())
                {
                    // ★ 延迟到主窗口完成首次渲染、消息队列空闲后再显示桌宠。
                    //   在 OnStartup 阶段直接 Show 一个 AllowsTransparency 置顶窗口，
                    //   其 HWND/合成管线尚未就绪，可能出现窗口已创建但不渲染（看不见），
                    //   必须隐藏再显示（即用户重新拨动开关）才出现的问题。
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { PetWindow?.ShowPet(); }
                        catch (Exception ex) { Debug.WriteLine($"[Pet] 延迟显示桌宠失败: {ex.Message}"); }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pet] PetWindow 初始化失败: {ex.Message}");
            }

            // Cleanup on exit
            Exit += (_, _) =>
            {
                // 0. 关闭桌宠窗口并保存设置
                try { PetWindow?.Close(); } catch { }
                _notifyIcon?.Dispose();
                _notifyIcon = null;

                // 1. 停止各本地 HTTP API 和后台服务
                try { _apiServer?.Stop(); } catch { }
                try { _executionServer?.Stop(); } catch { }
                try { _crawlerService?.Stop(); } catch { }
                // 停止 LSP API server + 所有 LSP 子进程（PHP Intelephense / Python Pyright / tsserver）
                // LspApiServer.Stop 内部会调用 _lspManager.StopAll() 释放所有 LSP 进程
                try { _lspApiServer?.Stop(); } catch { }

                // 2. 停止浏览器自动化：先停 HTTP 监听，再释放 PuppeteerSharp 浏览器
                // 带硬超时，避免 Dispose 内部 Wait 15秒导致主进程无法退出
                try
                {
                    var browserApiStop = _browserApiServer?.StopAsync();
                    browserApiStop?.Wait(TimeSpan.FromSeconds(3));
                }
                catch { }
                try
                {
                    var browserServiceStop = _browserService?.StopAsync();
                    browserServiceStop?.Wait(TimeSpan.FromSeconds(5));
                }
                catch { }

                // 3. 关闭 CEF（带超时，避免子进程无响应时卡住）
                try
                {
                    var cefShutdown = System.Threading.Tasks.Task.Run(() => Cef.Shutdown());
                    cefShutdown.Wait(TimeSpan.FromSeconds(5));
                    WaitForChildProcessExit("CefSharp.BrowserSubprocess", timeoutMs: 2000);
                }
                catch { }

                // 4. 强制清理仍未退出的 CefSharp 子进程
                KillRemainingProcesses("CefSharp.BrowserSubprocess");

                // 5. 确保主进程自身退出，避免后台线程/隐藏窗口导致残留
                Environment.Exit(0);
            };
        }

        private static void WaitForChildProcessExit(string processName, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var procs = Process.GetProcessesByName(processName);
                try
                {
                    if (procs.Length == 0) return;
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
                Thread.Sleep(300);
            }
        }

        private static void KillRemainingProcesses(string processName)
        {
            var currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!proc.HasExited && proc.Id != currentPid)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }

        // ── 读取 pet_settings.json 的 enabled 字段（决定启动时是否默认显示桌宠）──
        private static bool LoadPetEnabledFromSettings()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MoonYa", "pet_settings.json");
                if (!File.Exists(path)) return true; // 首次启动默认开启桌宠
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                return !doc.TryGetProperty("enabled", out var en) ||
                       en.ValueKind == JsonValueKind.True;
            }
            catch { return true; }
        }

        // ── 读取 launcher_config.json 的 browser_automation 段，构造 BrowserAutomationConfig ──
        // 缺失字段时使用 BrowserAutomationConfig 的默认值（Port=58905, Headless=false 等）
        // trusted_domains 不在此处读取：BrowserSecurityGate 自己直接从配置文件读取白名单
        private static BrowserAutomationConfig LoadBrowserAutomationConfig(string configPath)
        {
            var config = new BrowserAutomationConfig();
            if (!File.Exists(configPath)) return config;

            try
            {
                var json = File.ReadAllText(configPath);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                if (!doc.TryGetProperty("browser_automation", out var ba)) return config;

                if (ba.TryGetProperty("port", out var p) && p.TryGetInt32(out var port))
                    config.Port = port;
                if (ba.TryGetProperty("chromium_executable_path", out var cp) && cp.ValueKind == JsonValueKind.String)
                    config.ChromiumExecutablePath = cp.GetString() ?? string.Empty;
                if (ba.TryGetProperty("headless", out var h) &&
                    (h.ValueKind == JsonValueKind.True || h.ValueKind == JsonValueKind.False))
                    config.Headless = h.GetBoolean();
                if (ba.TryGetProperty("auto_download_chromium", out var ad) &&
                    (ad.ValueKind == JsonValueKind.True || ad.ValueKind == JsonValueKind.False))
                    config.AutoDownloadChromium = ad.GetBoolean();
                if (ba.TryGetProperty("default_timeout_ms", out var t) && t.TryGetInt32(out var timeout))
                    config.DefaultTimeoutMs = timeout;
                if (ba.TryGetProperty("element_timeout_ms", out var et) && et.TryGetInt32(out var elementTimeout))
                    config.ElementTimeoutMs = elementTimeout;
                if (ba.TryGetProperty("viewport_width", out var vw) && vw.TryGetInt32(out var viewportW))
                    config.ViewportWidth = viewportW;
                if (ba.TryGetProperty("viewport_height", out var vh) && vh.TryGetInt32(out var viewportH))
                    config.ViewportHeight = viewportH;
            }
            catch
            {
                // 解析失败：返回默认配置，不阻塞主程序启动
            }
            return config;
        }
    }
}
