// BrowserApiServer — HTTP API 服务，封装 BrowserAutomationService 与 BrowserSecurityGate，监听 127.0.0.1:58905

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MoonYa.Services
{
    /// <summary>
    /// 浏览器自动化 HTTP API 服务：基于 HttpListener 监听 127.0.0.1:58905，
    /// 暴露 start/navigate/click/fill/screenshot/scroll/get-text/evaluate/wait/stop/status 端点，
    /// 每个写操作完成后自动截图并在响应中返回 base64；失败直接返回错误，不降级、不重试。
    /// </summary>
    public class BrowserApiServer
    {
        private const int DefaultPort = 58905;

        private readonly BrowserAutomationService _service;
        private readonly BrowserSecurityGate _securityGate;
        private readonly int _port;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private int _screenshotIndex;

        public int Port => _port;
        public bool IsRunning => _listener?.IsListening ?? false;

        public BrowserApiServer(BrowserAutomationService service, BrowserSecurityGate securityGate, int port = DefaultPort)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _securityGate = securityGate ?? throw new ArgumentNullException(nameof(securityGate));
            _port = port;
        }

        // 启动 HttpListener，循环接收请求
        public Task StartAsync()
        {
            if (IsRunning) return Task.CompletedTask;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                System.Diagnostics.Debug.WriteLine($"BrowserApiServer: Failed to start on port {_port}: {ex.Message}");
                return Task.CompletedTask;
            }

            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            System.Diagnostics.Debug.WriteLine($"BrowserApiServer: Started on http://127.0.0.1:{_port}/");
            return Task.CompletedTask;
        }

        // 停止监听并释放资源
        public async Task StopAsync()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }

            if (_listenTask != null)
            {
                try { await _listenTask; } catch { }
            }

            System.Diagnostics.Debug.WriteLine("BrowserApiServer: Stopped.");
        }

        // 监听循环：异步接收请求并分发到 HandleRequest
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
                    System.Diagnostics.Debug.WriteLine($"BrowserApiServer: {ex.Message}");
                }
            }
        }

        // 处理单个 HTTP 请求：解析路径 + 方法 → 分发到对应端点
        private async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // CORS preflight：浏览器跨域预检直接 204 返回
                if (request.HttpMethod == "OPTIONS")
                {
                    SetCorsHeaders(response);
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                // 所有响应都附加 CORS 头（与 FileOperationApiServer 保持一致）
                SetCorsHeaders(response);

                // 仅允许本地访问
                if (!request.RemoteEndPoint!.Address.Equals(IPAddress.Loopback) &&
                    !request.RemoteEndPoint!.Address.Equals(IPAddress.IPv6Loopback))
                {
                    await Respond(response, 200, Json(new { success = false, error = "只允许本地访问" }));
                    return;
                }

                var path = request.Url!.AbsolutePath;
                var method = request.HttpMethod;

                // POST 请求体读取：用 StreamReader 读全文，再由各 handler 用 JsonElement 解析
                string body = string.Empty;
                if (method == "POST")
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        body = await reader.ReadToEndAsync();
                    }
                }

                // 路由分发：按 path + method 严格匹配
                switch (path)
                {
                    case "/browser/start" when method == "POST":
                        await HandleStart(response, body);
                        return;
                    case "/browser/navigate" when method == "POST":
                        await HandleNavigate(response, body);
                        return;
                    case "/browser/click" when method == "POST":
                        await HandleClick(response, body);
                        return;
                    case "/browser/fill" when method == "POST":
                        await HandleFill(response, body);
                        return;
                    case "/browser/screenshot" when method == "GET" || method == "POST":
                        await HandleScreenshot(response);
                        return;
                    case "/browser/scroll" when method == "POST":
                        await HandleScroll(response, body);
                        return;
                    case "/browser/get-text" when method == "POST":
                        await HandleGetText(response, body);
                        return;
                    case "/browser/get-elements" when method == "GET" || method == "POST":
                        await HandleGetElements(response);
                        return;
                    case "/browser/evaluate" when method == "POST":
                        await HandleEvaluate(response, body);
                        return;
                    case "/browser/wait" when method == "POST":
                        await HandleWait(response, body);
                        return;
                    case "/browser/stop" when method == "POST":
                        await HandleStop(response);
                        return;
                    case "/browser/status" when method == "GET" || method == "POST":
                        await HandleStatus(response);
                        return;
                    default:
                        await Respond(response, 200, Json(new { success = false, error = $"未知路由: {method} {path}" }));
                        return;
                }
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = $"内部错误: {ex.Message}" }));
            }
        }

        // /browser/start：安全授权后启动浏览器并导航到 url，自动截图
        private async Task HandleStart(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var url = TryGetString(req, "url");

                if (string.IsNullOrWhiteSpace(url))
                {
                    await Respond(response, 200, Json(new { success = false, error = "缺少 url 参数" }));
                    return;
                }

                // 安全授权：在调用 StartAsync 之前
                var auth = await _securityGate.RequestAuthorizationAsync(url!, new List<string> { "导航到 " + url });
                if (!auth.Approved)
                {
                    await Respond(response, 200, Json(new { success = false, error = "用户拒绝授权" }));
                    return;
                }

                await _service.StartAsync(url!);
                var screenshot = await _service.ScreenshotBase64Async();
                _screenshotIndex++;

                await Respond(response, 200, Json(new { success = true, screenshot, index = _screenshotIndex }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/navigate：安全授权后导航到新 url，自动截图
        private async Task HandleNavigate(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var url = TryGetString(req, "url");

                if (string.IsNullOrWhiteSpace(url))
                {
                    await Respond(response, 200, Json(new { success = false, error = "缺少 url 参数" }));
                    return;
                }

                // 安全授权：在调用 NavigateAsync 之前
                var auth = await _securityGate.RequestAuthorizationAsync(url!, new List<string> { "导航到 " + url });
                if (!auth.Approved)
                {
                    await Respond(response, 200, Json(new { success = false, error = "用户拒绝授权" }));
                    return;
                }

                await _service.NavigateAsync(url!);
                var screenshot = await _service.ScreenshotBase64Async();
                _screenshotIndex++;

                await Respond(response, 200, Json(new { success = true, screenshot, index = _screenshotIndex }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/click：点击 CSS 选择器元素，自动截图
        private async Task HandleClick(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var selector = TryGetString(req, "selector");

                if (string.IsNullOrWhiteSpace(selector))
                {
                    await Respond(response, 200, Json(new { success = false, error = "缺少 selector 参数" }));
                    return;
                }

                await _service.ClickAsync(selector!);
                var screenshot = await _service.ScreenshotBase64Async();
                _screenshotIndex++;

                await Respond(response, 200, Json(new { success = true, screenshot, index = _screenshotIndex }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/fill：向元素输入文本，自动截图
        private async Task HandleFill(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var selector = TryGetString(req, "selector");
                var text = TryGetString(req, "text") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(selector))
                {
                    await Respond(response, 200, Json(new { success = false, error = "缺少 selector 参数" }));
                    return;
                }

                await _service.FillAsync(selector!, text);
                var screenshot = await _service.ScreenshotBase64Async();
                _screenshotIndex++;

                await Respond(response, 200, Json(new { success = true, screenshot, index = _screenshotIndex }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/screenshot：仅返回当前视口截图
        private async Task HandleScreenshot(HttpListenerResponse response)
        {
            try
            {
                var screenshot = await _service.ScreenshotBase64Async();
                _screenshotIndex++;

                await Respond(response, 200, Json(new { success = true, screenshot, index = _screenshotIndex }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/scroll：滚动页面，自动截图
        private async Task HandleScroll(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var direction = TryGetString(req, "direction") ?? "down";
                var amount = TryGetInt(req, "amount") ?? 300;

                await _service.ScrollAsync(direction, amount);
                var screenshot = await _service.ScreenshotBase64Async();
                _screenshotIndex++;

                await Respond(response, 200, Json(new { success = true, screenshot, index = _screenshotIndex }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/get-text：获取元素 innerText（不截图）
        private async Task HandleGetText(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var selector = TryGetString(req, "selector");

                if (string.IsNullOrWhiteSpace(selector))
                {
                    await Respond(response, 200, Json(new { success = false, error = "缺少 selector 参数" }));
                    return;
                }

                var text = await _service.GetTextAsync(selector!);
                await Respond(response, 200, Json(new { success = true, text }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/get-elements：获取页面所有可交互元素的 DOM 信息（100% 准确的 CSS 选择器）
        private async Task HandleGetElements(HttpListenerResponse response)
        {
            try
            {
                var elementsJson = await _service.GetElementsAsync();
                // 反序列化为对象再序列化，避免 JSON 字符串被当作普通字符串双重转义
                var elements = JsonSerializer.Deserialize<object>(elementsJson ?? "[]");
                await Respond(response, 200, Json(new { success = true, elements }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/evaluate：执行任意 JS 表达式并返回 JSON 序列化结果
        private async Task HandleEvaluate(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var jsCode = TryGetString(req, "js_code");

                if (string.IsNullOrWhiteSpace(jsCode))
                {
                    await Respond(response, 200, Json(new { success = false, error = "缺少 js_code 参数" }));
                    return;
                }

                var result = await _service.EvaluateAsync(jsCode!);
                await Respond(response, 200, Json(new { success = true, result }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/wait：单纯延迟，不操作浏览器，不截图
        private async Task HandleWait(HttpListenerResponse response, string body)
        {
            try
            {
                var req = ParseBody(body);
                var ms = TryGetInt(req, "ms") ?? 1000;

                await _service.WaitAsync(ms);
                await Respond(response, 200, Json(new { success = true }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/stop：关闭浏览器并释放资源，不截图
        private async Task HandleStop(HttpListenerResponse response)
        {
            try
            {
                await _service.StopAsync();
                await Respond(response, 200, Json(new { success = true }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // /browser/status：返回浏览器当前运行状态对象
        private async Task HandleStatus(HttpListenerResponse response)
        {
            try
            {
                var status = await _service.GetStatusAsync();
                await Respond(response, 200, Json(new { success = true, status }));
            }
            catch (Exception ex)
            {
                await Respond(response, 200, Json(new { success = false, error = ex.Message }));
            }
        }

        // ── 内部辅助 ───────────────────────────────────────

        // 解析 JSON 请求体为 JsonElement；空 body 返回空对象
        private static JsonElement ParseBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return JsonSerializer.Deserialize<JsonElement>("{}");
            }
            return JsonSerializer.Deserialize<JsonElement>(body);
        }

        // 从 JsonElement 安全读取字符串属性；不存在或类型不匹配返回 null
        private static string? TryGetString(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }
            return null;
        }

        // 从 JsonElement 安全读取 int 属性；不存在或类型不匹配返回 null
        private static int? TryGetInt(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var p) &&
                p.ValueKind == JsonValueKind.Number &&
                p.TryGetInt32(out var v))
            {
                return v;
            }
            return null;
        }

        // 写响应：HTTP 状态码 + JSON body
        private static async Task Respond(HttpListenerResponse response, int statusCode, string body)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        // CORS 头：与 FileOperationApiServer.SetCorsHeaders 保持一致
        private static void SetCorsHeaders(HttpListenerResponse response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");
            // Private Network Access：允许 HTTPS 页面跨域请求本地 HTTP API（Chrome 94+ 要求）
            response.Headers.Add("Access-Control-Allow-Private-Network", "true");
        }

        // 序列化对象为 camelCase JSON
        private static string Json(object obj) =>
            JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
