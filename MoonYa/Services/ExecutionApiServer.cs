using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MoonYa.Services
{
    public class ExecutionApiServer
    {
        private readonly CommandExecutionService _commandService;
        private readonly PythonExecutionService _pythonService;
        private readonly ExecutionHistoryService _historyService;
        private readonly RiskAssessmentService _riskService;
        private readonly SandboxService _sandboxService;
        private readonly int _port;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;

        public int Port => _port;
        public bool IsRunning => _listener?.IsListening ?? false;

        public ExecutionApiServer(int port = 58903)
        {
            _port = port;
            _riskService = new RiskAssessmentService();
            _sandboxService = new SandboxService();

            int commandTimeout = 60;
            int pythonTimeout = 30;
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");
                if (!File.Exists(configPath))
                    configPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "launcher_config.json"));
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("execution_tools", out var execTools) &&
                        execTools.TryGetProperty("sandbox", out var sandbox))
                    {
                        if (sandbox.TryGetProperty("command_timeout_sec", out var ct) && ct.TryGetInt32(out var ctv))
                            commandTimeout = ctv;
                        if (sandbox.TryGetProperty("python_timeout_sec", out var pt) && pt.TryGetInt32(out var ptv))
                            pythonTimeout = ptv;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecutionApiServer: Failed to load timeout config: {ex.Message}");
            }

            _commandService = new CommandExecutionService(_riskService, _sandboxService, commandTimeout);
            _pythonService = new PythonExecutionService(_riskService, _sandboxService, pythonTimeout);
            _historyService = new ExecutionHistoryService();
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
                System.Diagnostics.Debug.WriteLine($"ExecutionApiServer: Failed to start on port {_port}: {ex.Message}");
                return;
            }

            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            System.Diagnostics.Debug.WriteLine($"ExecutionApiServer: Started on http://127.0.0.1:{_port}/");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _commandService.Shutdown();
            System.Diagnostics.Debug.WriteLine("ExecutionApiServer: Stopped.");
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
                    System.Diagnostics.Debug.WriteLine($"ExecutionApiServer error: {ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var path = request.Url?.AbsolutePath ?? "/";

                if (request.HttpMethod == "OPTIONS")
                {
                    // CORS preflight
                    SetCorsHeaders(response);
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                SetCorsHeaders(response);

                if (path == "/execute" && request.HttpMethod == "POST")
                {
                    await HandleExecute(request, response);
                }
                else if (path == "/history" && request.HttpMethod == "GET")
                {
                    await HandleHistory(request, response);
                }
                else if (path == "/health" && request.HttpMethod == "GET")
                {
                    await SendJsonResponse(response, new { status = "ok", python_available = _pythonService.IsPythonAvailable(), auto_approve_default = true });
                }
                else
                {
                    response.StatusCode = 404;
                    await SendJsonResponse(response, new { error = "Not Found", path = path });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecutionApiServer request error: {ex.Message}");
                try
                {
                    response.StatusCode = 500;
                    await SendJsonResponse(response, new { error = ex.Message });
                }
                catch { }
            }
        }

        // POST /execute - Execute command or python script
        private async Task HandleExecute(HttpListenerRequest request, HttpListenerResponse response)
        {
            // Read request body
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                response.StatusCode = 400;
                await SendJsonResponse(response, new { error = "Empty request body" });
                return;
            }

            // Parse JSON request
            ExecuteRequest? execRequest;
            try
            {
                execRequest = JsonSerializer.Deserialize<ExecuteRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                response.StatusCode = 400;
                await SendJsonResponse(response, new { error = "Invalid JSON body" });
                return;
            }

            if (execRequest == null || string.IsNullOrWhiteSpace(execRequest.Code))
            {
                response.StatusCode = 400;
                await SendJsonResponse(response, new { error = "Missing 'code' field in request" });
                return;
            }

            var execType = execRequest.Type?.ToLower() ?? "command";
            if (execType != "command" && execType != "python")
            {
                response.StatusCode = 400;
                await SendJsonResponse(response, new { error = $"Invalid type '{execType}'. Must be 'command' or 'python'." });
                return;
            }

            // Check if client wants SSE streaming
            var acceptHeader = request.Headers["Accept"] ?? "";
            var useSse = acceptHeader.Contains("text/event-stream");

            if (useSse)
            {
                // SSE streaming mode
                response.ContentType = "text/event-stream";
                response.Headers.Add("Cache-Control", "no-cache");
                response.Headers.Add("Connection", "keep-alive");

                using var responseStream = response.OutputStream;
                using var writer = new StreamWriter(responseStream, Encoding.UTF8) { AutoFlush = true };

                // Send initial event
                await SendSseEvent(writer, "start", new { type = execType, code = execRequest.Code });

                // CommandExecutionService.ExecuteAsync 返回 object 以兼容 blocking=false 后台模式。
                // 此处 /execute 端点未透传 blocking 参数（默认 null=同步），
                // 因此 command 路径返回的 object 实际上是 ExecutionResult；python 路径仍返回 ExecutionResult。
                object result;
                if (execType == "python")
                {
                    result = await _pythonService.ExecuteAsync(execRequest.Code, execRequest.Params?.Args, execRequest.AutoApprove, execRequest.Cwd, execRequest.Params?.TimeoutSec);
                }
                else
                {
                    result = await _commandService.ExecuteAsync(execRequest.Code, execRequest.AutoApprove, execRequest.Cwd, execRequest.Params?.TimeoutSec);
                }

                // Send result event
                await SendSseEvent(writer, "result", result);

                // Record history（仅同步模式返回 ExecutionResult 时记录；后台模式返回 command_id 对象时跳过）
                if (result is ExecutionResult er1)
                {
                    _historyService.Record(execType, er1);
                }

                // Send done event
                await SendSseEvent(writer, "done", new { message = "Execution complete" });

                response.Close();
            }
            else
            {
                // Simple JSON response mode
                object result;
                if (execType == "python")
                {
                    result = await _pythonService.ExecuteAsync(execRequest.Code, execRequest.Params?.Args, execRequest.AutoApprove, execRequest.Cwd, execRequest.Params?.TimeoutSec);
                }
                else
                {
                    result = await _commandService.ExecuteAsync(execRequest.Code, execRequest.AutoApprove, execRequest.Cwd, execRequest.Params?.TimeoutSec);
                }

                // Record history（仅同步模式返回 ExecutionResult 时记录；后台模式返回 command_id 对象时跳过）
                if (result is ExecutionResult er2)
                {
                    _historyService.Record(execType, er2);
                }

                await SendJsonResponse(response, result);
            }
        }

        // GET /history - Get execution history
        private async Task HandleHistory(HttpListenerRequest request, HttpListenerResponse response)
        {
            var typeFilter = request.QueryString["type"]; // optional: "command" or "python"
            var limitStr = request.QueryString["limit"];
            int limit = 50;
            if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out var parsed))
                limit = Math.Min(parsed, 200);

            List<ExecutionHistoryRecord> records;
            if (!string.IsNullOrEmpty(typeFilter))
                records = _historyService.GetHistoryByType(typeFilter, limit);
            else
                records = _historyService.GetHistory(limit);

            await SendJsonResponse(response, new { count = records.Count, records = records });
        }

        // ── helper methods ──

        private static void SetCorsHeaders(HttpListenerResponse response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");
            response.Headers.Add("Access-Control-Allow-Private-Network", "true");
        }

        private static async Task SendJsonResponse(HttpListenerResponse response, object data)
        {
            response.ContentType = "application/json; charset=utf-8";
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }

        private static async Task SendSseEvent(StreamWriter writer, string eventName, object data)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            await writer.WriteAsync($"event: {eventName}\ndata: {json}\n\n");
        }
    }

    // ── Request/Response DTOs ──

    public class ExecuteRequest
    {
        public string Type { get; set; } = "command"; // "command" or "python"
        public string Code { get; set; } = "";
        public ExecuteParams? Params { get; set; }
        public string? SessionId { get; set; }
        public string? Cwd { get; set; }            // working directory for command execution
        public bool AutoApprove { get; set; } = true; // skip Medium-risk confirmation dialog
    }

    public class ExecuteParams
    {
        public string? Args { get; set; }  // Python script arguments
        public int? TimeoutSec { get; set; } // Custom timeout
    }
}
