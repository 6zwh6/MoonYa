// UiAutomationService — 基于 System.Windows.Automation 的元素级 UI 自动化服务（CU 模式主路径，ComputerUseService 作为回退）

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using Condition = System.Windows.Automation.Condition;

namespace MoonYa.Services
{
    /// <summary>
    /// 基于 UIA (System.Windows.Automation) 的元素级 UI 自动化服务。
    /// 取代基于坐标的 SendInput 作为 CU 模式主路径；ComputerUseService 作为回退。
    /// 所有公共方法返回 object（匿名类型），永不抛出异常。
    /// </summary>
    public class UiAutomationService
    {
        private readonly ComputerUseService _cuService;
        private readonly ElementCache _cache;
        private readonly object _cacheLock = new object();

        // 类常量仅作兜底默认值（运行时参数需由调用方传入）
        private const int DefaultMaxDepth = 6;
        private const int MaxMaxDepth = 10;
        private const int DefaultMaxNodes = 2000;

        public UiAutomationService(ComputerUseService cuService)
        {
            _cuService = cuService ?? throw new ArgumentNullException(nameof(cuService));
            _cache = new ElementCache();
        }

        // ── Public API ─────────────────────────────────────

        /// <summary>
        /// 查找当前活动窗口：包含焦点元素的顶级 Window；若无焦点或无 Window 祖先，
        /// 则返回根元素的第一个子窗口。
        /// </summary>
        public AutomationElement? FindActiveWindow()
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused != null)
                {
                    var walker = TreeWalker.RawViewWalker;
                    var current = focused;
                    while (current != null)
                    {
                        try
                        {
                            if (current.Current.ControlType == ControlType.Window)
                            {
                                return current;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"UiAutomationService: FindActiveWindow ControlType check failed: {ex.Message}");
                        }

                        AutomationElement? parent = null;
                        try
                        {
                            parent = walker.GetParent(current);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"UiAutomationService: FindActiveWindow GetParent failed: {ex.Message}");
                            break;
                        }

                        if (parent == null) break;
                        current = parent;
                    }
                }

                // 回退：根的第一个子窗口
                var root = AutomationElement.RootElement;
                try
                {
                    return TreeWalker.RawViewWalker.GetFirstChild(root);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: FindActiveWindow GetFirstChild(root) failed: {ex.Message}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: FindActiveWindow failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 根据条件查找元素。automationId 精确匹配；name 大小写不敏感包含匹配；
        /// controlType 按名称（如 "Button"/"Edit"/"CheckBox"）匹配。
        /// 找到后缓存并返回 element_id。
        /// </summary>
        public object FindElement(string? parentElementId, string? automationId, string? name, string? controlType)
        {
            try
            {
                AutomationElement? parent = null;
                if (!string.IsNullOrEmpty(parentElementId))
                {
                    parent = GetCachedElement(parentElementId);
                    if (parent == null)
                    {
                        return new { success = false, error = "element_expired", suggestion = "re-run find_element" };
                    }
                }
                if (parent == null)
                {
                    parent = FindActiveWindow();
                    if (parent == null)
                    {
                        return new { success = false, error = "no_active_window" };
                    }
                }

                // 构造条件
                var conditions = new List<Condition>();
                if (!string.IsNullOrEmpty(automationId))
                {
                    conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                }
                if (!string.IsNullOrEmpty(name))
                {
                    conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase));
                }
                if (!string.IsNullOrEmpty(controlType))
                {
                    try
                    {
                        var ct = LookupControlTypeByName(controlType);
                        if (ct != null)
                        {
                            conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: LookupControlTypeByName('{controlType}') failed: {ex.Message}");
                    }
                }

                if (conditions.Count == 0)
                {
                    return new
                    {
                        success = false,
                        error = "no_conditions",
                        suggestion = "provide at least one of: automation_id, name, control_type"
                    };
                }

                Condition finalCondition = conditions.Count == 1
                    ? conditions[0]
                    : new AndCondition(conditions.ToArray());

                var found = parent.FindFirst(TreeScope.Descendants, finalCondition);
                if (found == null)
                {
                    return new { success = false, error = "element_not_found" };
                }

                string elementId;
                lock (_cacheLock)
                {
                    elementId = _cache.GenerateElementId();
                    _cache.Store(elementId, found);
                }

                return new
                {
                    success = true,
                    element_id = elementId,
                    name = SafeName(found),
                    control_type = SafeControlType(found),
                    automation_id = SafeAutomationId(found),
                    bounding_rectangle = RectToObject(SafeGetRect(found)),
                    is_enabled = SafeIsEnabled(found),
                    is_offscreen = SafeIsOffscreen(found)
                };
            }
            catch (ElementNotAvailableException ex)
            {
                return new { success = false, error = "element_not_available", message = ex.Message };
            }
            catch (InvalidOperationException ex)
            {
                return new { success = false, error = "invalid_operation", message = ex.Message };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: FindElement failed: {ex.Message}");
                return new { success = false, error = "internal_error", message = ex.Message };
            }
        }

        /// <summary>
        /// 获取 UI 树。rootElementId 为空时使用活动窗口。
        /// format: "text"（qoder 风格缩进文本，默认）或 "json"（v1 JSON 结构）。
        /// maxDepth 默认 DefaultMaxDepth(6)，上限 MaxMaxDepth(10)。
        /// maxNodes 默认 DefaultMaxNodes(2000)，超出时截断并标记 _truncated。
        /// 返回值包含 format 字段标记当前序列化格式；tree 字段为文本字符串或 JSON 对象。
        /// </summary>
        public object GetUiTree(string? rootElementId, int maxDepth, string format = "text", int maxNodes = 0)
        {
            try
            {
                if (maxDepth <= 0) maxDepth = DefaultMaxDepth;
                if (maxDepth > MaxMaxDepth) maxDepth = MaxMaxDepth;
                if (maxNodes <= 0) maxNodes = DefaultMaxNodes;
                if (string.IsNullOrEmpty(format)) format = "text";
                format = format.ToLowerInvariant();

                AutomationElement? root = null;
                if (!string.IsNullOrEmpty(rootElementId))
                {
                    root = GetCachedElement(rootElementId);
                    if (root == null)
                    {
                        return new { success = false, error = "element_expired", suggestion = "re-run find_element" };
                    }
                }
                if (root == null)
                {
                    root = FindActiveWindow();
                    if (root == null)
                    {
                        return new { success = false, error = "no_active_window" };
                    }
                }

                if (format == "json")
                {
                    // v1 JSON 行为
                    var counter = new TreeCounter { Count = 0, Limit = maxNodes, Truncated = false };
                    var tree = BuildTreeNode(root, 0, maxDepth, counter);
                    return new
                    {
                        success = true,
                        format = "json",
                        tree = tree,
                        element_count = counter.Count,
                        _truncated = counter.Truncated,
                        _total_nodes = counter.Count
                    };
                }

                // 默认 text 格式（qoder 风格）
                AutomationElement? focused = null;
                try { focused = AutomationElement.FocusedElement; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: GetUiTree FocusedElement failed: {ex.Message}");
                }

                var (treeText, elementCount, truncated, _, _, _) = SerializeTreeToTextInternal(root, focused, maxDepth, maxNodes);
                return new
                {
                    success = true,
                    format = "text",
                    tree = treeText,
                    element_count = elementCount,
                    _truncated = truncated,
                    _total_nodes = elementCount
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: GetUiTree failed: {ex.Message}");
                return new { success = false, error = "internal_error", message = ex.Message };
            }
        }

        /// <summary>
        /// 点击元素。优先 InvokePattern.Invoke()，其次 TogglePattern.Toggle()，
        /// 最后回退到 ComputerUseService.MouseClick(centerX, centerY)。
        /// </summary>
        public object ClickElement(string elementId)
        {
            try
            {
                var element = GetCachedElement(elementId);
                if (element == null)
                {
                    return new { success = false, error = "element_expired", suggestion = "re-run find_element" };
                }

                // 1. InvokePattern
                try
                {
                    if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj) && invokeObj is InvokePattern invoke)
                    {
                        invoke.Invoke();
                        return new { success = true, method = "InvokePattern" };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: ClickElement InvokePattern failed: {ex.Message}");
                }

                // 2. TogglePattern
                try
                {
                    if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObj) && toggleObj is TogglePattern toggle)
                    {
                        toggle.Toggle();
                        return new { success = true, method = "TogglePattern" };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: ClickElement TogglePattern failed: {ex.Message}");
                }

                // 3. SendInput 鼠标点击回退
                var rect = SafeGetRect(element);
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                {
                    return new { success = false, error = "no_clickable_point", message = "元素无可点击区域" };
                }
                int centerX = (int)(rect.X + rect.Width / 2);
                int centerY = (int)(rect.Y + rect.Height / 2);
                _cuService.MouseClick(centerX, centerY, "left", "single");
                return new { success = true, method = "SendInput", x = centerX, y = centerY };
            }
            catch (ElementNotAvailableException ex)
            {
                return new { success = false, error = "element_not_available", message = ex.Message };
            }
            catch (ElementNotEnabledException ex)
            {
                return new { success = false, error = "element_not_enabled", message = ex.Message };
            }
            catch (InvalidOperationException ex)
            {
                return new { success = false, error = "invalid_operation", message = ex.Message };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: ClickElement failed: {ex.Message}");
                return new { success = false, error = "internal_error", message = ex.Message };
            }
        }

        /// <summary>
        /// 设置元素文本。优先 ValuePattern.SetValue(text)；
        /// 回退：focus + 清空 + ComputerUseService.KeyboardType(text)。
        /// </summary>
        public object SetText(string elementId, string text)
        {
            try
            {
                var element = GetCachedElement(elementId);
                if (element == null)
                {
                    return new { success = false, error = "element_expired", suggestion = "re-run find_element" };
                }

                // 1. ValuePattern
                try
                {
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) && valueObj is ValuePattern value)
                    {
                        value.SetValue(text ?? "");
                        return new { success = true, method = "ValuePattern" };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: SetText ValuePattern failed: {ex.Message}");
                }

                // 2. 回退：focus + 清空 + KeyboardType
                try
                {
                    element.SetFocus();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: SetText SetFocus failed: {ex.Message}");
                }

                // 尝试清空已有内容
                try
                {
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) && valueObj is ValuePattern value)
                    {
                        value.SetValue("");
                    }
                    else
                    {
                        _cuService.KeyPress("ctrl+a");
                        _cuService.KeyPress("delete");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: SetText clear failed: {ex.Message}");
                }

                _cuService.KeyboardType(text ?? "");
                return new { success = true, method = "KeyboardType" };
            }
            catch (ElementNotAvailableException ex)
            {
                return new { success = false, error = "element_not_available", message = ex.Message };
            }
            catch (ElementNotEnabledException ex)
            {
                return new { success = false, error = "element_not_enabled", message = ex.Message };
            }
            catch (InvalidOperationException ex)
            {
                return new { success = false, error = "invalid_operation", message = ex.Message };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: SetText failed: {ex.Message}");
                return new { success = false, error = "internal_error", message = ex.Message };
            }
        }

        /// <summary>
        /// 获取元素文本。优先 TextPattern.DocumentRange.GetText(-1)，
        /// 其次 ValuePattern.Current.Value，最后 element.Current.Name。
        /// 超过 5000 字符截断并追加 [已截断]。
        /// </summary>
        public object GetText(string elementId)
        {
            try
            {
                var element = GetCachedElement(elementId);
                if (element == null)
                {
                    return new { success = false, error = "element_expired", suggestion = "re-run find_element" };
                }

                string? text = null;
                string method = "Name";

                // 1. TextPattern
                try
                {
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObj) && textObj is TextPattern textPattern)
                    {
                        text = textPattern.DocumentRange.GetText(-1);
                        method = "TextPattern";
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: GetText TextPattern failed: {ex.Message}");
                }

                // 2. ValuePattern
                if (string.IsNullOrEmpty(text))
                {
                    try
                    {
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) && valueObj is ValuePattern value)
                        {
                            text = value.Current.Value;
                            method = "ValuePattern";
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: GetText ValuePattern failed: {ex.Message}");
                    }
                }

                // 3. Name
                if (string.IsNullOrEmpty(text))
                {
                    try
                    {
                        text = element.Current.Name;
                        method = "Name";
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: GetText Name failed: {ex.Message}");
                    }
                }

                text ??= "";
                bool truncated = false;
                if (text.Length > 5000)
                {
                    text = text.Substring(0, 5000) + "[已截断]";
                    truncated = true;
                }

                return new
                {
                    success = true,
                    text = text,
                    method = method,
                    truncated = truncated,
                    length = text.Length
                };
            }
            catch (ElementNotAvailableException ex)
            {
                return new { success = false, error = "element_not_available", message = ex.Message };
            }
            catch (InvalidOperationException ex)
            {
                return new { success = false, error = "invalid_operation", message = ex.Message };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: GetText failed: {ex.Message}");
                return new { success = false, error = "internal_error", message = ex.Message };
            }
        }

        /// <summary>
        /// 枚举所有可见的顶层窗口，返回按 Z-order（顶到底）排列的窗口清单。
        /// 过滤：IsWindowVisible && GetWindowTextLength > 0 && 非 WS_EX_TOOLWINDOW。
        /// </summary>
        public List<WindowInfo> GetWindowSnapshot()
        {
            var list = new List<WindowInfo>();
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd)) return true;

                        // 排除工具窗口（WS_EX_TOOLWINDOW）
                        var exStyle = GetWindowLongCompat(hWnd, GWL_EXSTYLE);
                        if ((exStyle.ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;

                        int titleLen = GetWindowTextLength(hWnd);
                        if (titleLen <= 0) return true;

                        var sb = new StringBuilder(titleLen + 1);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        var title = sb.ToString();

                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);

                        string processName = "";
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            processName = proc.ProcessName;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"UiAutomationService: GetWindowSnapshot GetProcessById({pid}) failed: {ex.Message}");
                        }

                        list.Add(new WindowInfo
                        {
                            Title = title,
                            ProcessName = processName,
                            ProcessId = (int)pid,
                            Hwnd = hWnd,
                            IsVisible = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: GetWindowSnapshot enum callback failed: {ex.Message}");
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: GetWindowSnapshot failed: {ex.Message}");
            }
            return list;
        }

        // ── Private helpers ────────────────────────────────

        /// <summary>
        /// Task 11: 一次性合成 UI 快照——UIA 树 + 截图 + 焦点元素 + 窗口元信息。
        /// 参考 qoder CU 模式：单次调用同时返回 accessibility / screenshots / window / diagnostics。
        /// 所有运行时参数由调用方传入，类常量仅作兜底默认值。
        /// </summary>
        /// <param name="maxDepth">UIA 树最大深度（默认 6，上限 10）</param>
        /// <param name="includeScreenshot">是否包含截图（false 时 screenshots 为空数组）</param>
        /// <param name="screenshotTarget">截图目标 "window"（默认）或 "screen"</param>
        /// <returns>含 success / accessibility / screenshots / window / diagnostics 的匿名对象，永不抛出异常</returns>
        public object CaptureUiSnapshot(int maxDepth = 6, bool includeScreenshot = true, string screenshotTarget = "window")
        {
            try
            {
                // 参数兜底
                if (maxDepth <= 0) maxDepth = DefaultMaxDepth;
                if (maxDepth > MaxMaxDepth) maxDepth = MaxMaxDepth;
                if (string.IsNullOrEmpty(screenshotTarget)) screenshotTarget = "window";

                // 1. 获取活动窗口
                var window = FindActiveWindow();
                if (window == null)
                {
                    return new { success = false, error = "no_active_window" };
                }

                // 2. 获取焦点元素
                AutomationElement? focused = null;
                try { focused = AutomationElement.FocusedElement; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: CaptureUiSnapshot FocusedElement failed: {ex.Message}");
                }

                // 3. 序列化 UIA 树
                int maxNodes = DefaultMaxNodes;
                var (treeText, nodeCount, truncated, focusedIndex, focusedControlType, focusedName) =
                    SerializeTreeToTextInternal(window, focused, maxDepth, maxNodes);

                // 4. 构造 focused_element 摘要：{index} {control_type} {name}
                string focusedElementStr = focusedIndex >= 0
                    ? $"{focusedIndex} {focusedControlType} {focusedName}".Trim()
                    : "";

                // 5. 获取窗口 HWND
                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    object hwndObj = window.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty);
                    if (hwndObj is int intHwnd && intHwnd != 0)
                    {
                        hwnd = new IntPtr(intHwnd);
                    }
                    else if (hwndObj is IntPtr ptrHwnd && ptrHwnd != IntPtr.Zero)
                    {
                        hwnd = ptrHwnd;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: CaptureUiSnapshot NativeWindowHandleProperty failed: {ex.Message}");
                }

                // 6. 组装 window 元信息
                string appName = SafeProcessName(window);
                string windowTitle = SafeName(window);
                int processId = 0;
                try { processId = window.Current.ProcessId; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: CaptureUiSnapshot ProcessId failed: {ex.Message}");
                }

                IntPtr foregroundHwnd = GetForegroundWindow();
                bool isForeground = hwnd != IntPtr.Zero && hwnd == foregroundHwnd;
                bool isMinimized = hwnd != IntPtr.Zero && IsIconic(hwnd);

                Rect winRect = SafeGetRect(window);
                var bounds = new
                {
                    x = (int)winRect.X,
                    y = (int)winRect.Y,
                    width = (int)winRect.Width,
                    height = (int)winRect.Height
                };

                var windowInfo = new
                {
                    app = appName,
                    title = windowTitle,
                    process_id = processId,
                    hwnd = hwnd.ToInt64(),
                    is_foreground = isForeground,
                    is_minimized = isMinimized,
                    bounds = bounds
                };

                // 7. 截图（includeScreenshot=false 时跳过）
                var screenshots = new List<object>();
                if (includeScreenshot)
                {
                    try
                    {
                        object shotResult = _cuService.TakeScreenshot(screenshotTarget);
                        // 将 TakeScreenshot 的返回字段与 window 子对象合并为单对象（参考 qoder 格式）
                        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                        string shotJson = JsonSerializer.Serialize(shotResult, jsonOpts);
                        var shotNode = JsonSerializer.Deserialize<JsonObject>(shotJson, jsonOpts);
                        if (shotNode != null)
                        {
                            string winJson = JsonSerializer.Serialize(windowInfo, jsonOpts);
                            shotNode["window"] = JsonNode.Parse(winJson);
                            screenshots.Add(shotNode);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: CaptureUiSnapshot TakeScreenshot failed: {ex.Message}");
                    }
                }

                // 8. 组装最终返回
                return new
                {
                    success = true,
                    accessibility = new
                    {
                        focused_element = focusedElementStr,
                        node_count = nodeCount,
                        max_depth = maxDepth,
                        format = "text",
                        tree = treeText,
                        truncated = truncated
                    },
                    screenshots = screenshots,
                    window = windowInfo,
                    diagnostics = new
                    {
                        accessibility_provider = "uia-net",
                        screenshot_provider = "copyfromscreen",
                        node_count = nodeCount,
                        truncated = truncated
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: CaptureUiSnapshot failed: {ex.Message}");
                return new { success = false, error = "internal_error", message = ex.Message };
            }
        }

        /// <summary>
        /// 从缓存中获取元素。过期或未知 id 返回 null。
        /// </summary>
        private AutomationElement? GetCachedElement(string elementId)
        {
            lock (_cacheLock)
            {
                return _cache.Get(elementId);
            }
        }

        private static object RectToObject(Rect rect)
        {
            return new
            {
                x = (int)rect.X,
                y = (int)rect.Y,
                w = (int)rect.Width,
                h = (int)rect.Height
            };
        }

        private static string SafeName(AutomationElement element)
        {
            try { return element.Current.Name ?? ""; }
            catch { return ""; }
        }

        private static string SafeControlType(AutomationElement element)
        {
            try
            {
                return element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "";
            }
            catch { return ""; }
        }

        private static string SafeAutomationId(AutomationElement element)
        {
            try { return element.Current.AutomationId ?? ""; }
            catch { return ""; }
        }

        private static bool SafeIsEnabled(AutomationElement element)
        {
            try { return element.Current.IsEnabled; }
            catch { return false; }
        }

        private static bool SafeIsOffscreen(AutomationElement element)
        {
            try { return element.Current.IsOffscreen; }
            catch { return true; }
        }

        private static Rect SafeGetRect(AutomationElement element)
        {
            try { return element.Current.BoundingRectangle; }
            catch { return Rect.Empty; }
        }

        /// <summary>
        /// 按名称（大小写不敏感）查找 ControlType。ControlType.LookupByName 在此版本不存在，
        /// 因此手动映射常见控件类型名称到 ControlType 静态字段。
        /// </summary>
        private static ControlType? LookupControlTypeByName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return name.ToLowerInvariant() switch
            {
                "button" => ControlType.Button,
                "calendar" => ControlType.Calendar,
                "checkbox" => ControlType.CheckBox,
                "combobox" => ControlType.ComboBox,
                "custom" => ControlType.Custom,
                "datagrid" => ControlType.DataGrid,
                "dataitem" => ControlType.DataItem,
                "document" => ControlType.Document,
                "edit" => ControlType.Edit,
                "group" => ControlType.Group,
                "header" => ControlType.Header,
                "headeritem" => ControlType.HeaderItem,
                "hyperlink" => ControlType.Hyperlink,
                "image" => ControlType.Image,
                "list" => ControlType.List,
                "listitem" => ControlType.ListItem,
                "menu" => ControlType.Menu,
                "menubar" => ControlType.MenuBar,
                "menuitem" => ControlType.MenuItem,
                "pane" => ControlType.Pane,
                "progressbar" => ControlType.ProgressBar,
                "radiobutton" => ControlType.RadioButton,
                "scrollbar" => ControlType.ScrollBar,
                "separator" => ControlType.Separator,
                "slider" => ControlType.Slider,
                "splitbutton" => ControlType.SplitButton,
                "statusbar" => ControlType.StatusBar,
                "tab" => ControlType.Tab,
                "tabitem" => ControlType.TabItem,
                "table" => ControlType.Table,
                "text" => ControlType.Text,
                "thumb" => ControlType.Thumb,
                "titlebar" => ControlType.TitleBar,
                "toolbar" => ControlType.ToolBar,
                "tooltip" => ControlType.ToolTip,
                "tree" => ControlType.Tree,
                "treeitem" => ControlType.TreeItem,
                "window" => ControlType.Window,
                _ => null
            };
        }

        private object BuildTreeNode(AutomationElement element, int depth, int maxDepth, TreeCounter counter)
        {
            counter.Count++;
            if (counter.Count > counter.Limit)
            {
                counter.Truncated = true;
                return new
                {
                    name = "",
                    control_type = "",
                    automation_id = "",
                    bounding_rectangle = new { x = 0, y = 0, w = 0, h = 0 },
                    is_enabled = false,
                    is_offscreen = true,
                    children = new List<object>(),
                    _depth_limited = false,
                    _truncated = true
                };
            }

            string name = SafeName(element);
            string controlType = SafeControlType(element);
            string automationId = SafeAutomationId(element);
            Rect rect = SafeGetRect(element);
            bool isEnabled = SafeIsEnabled(element);
            bool isOffscreen = SafeIsOffscreen(element);

            var children = new List<object>();
            bool depthLimited = depth >= maxDepth;
            if (!depthLimited)
            {
                var walker = TreeWalker.RawViewWalker;
                AutomationElement? child = null;
                try
                {
                    child = walker.GetFirstChild(element);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: BuildTreeNode GetFirstChild failed: {ex.Message}");
                }

                while (child != null)
                {
                    if (counter.Count > counter.Limit)
                    {
                        counter.Truncated = true;
                        break;
                    }

                    try
                    {
                        children.Add(BuildTreeNode(child, depth + 1, maxDepth, counter));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: BuildTreeNode child build failed: {ex.Message}");
                    }

                    try
                    {
                        child = walker.GetNextSibling(child);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: BuildTreeNode GetNextSibling failed: {ex.Message}");
                        break;
                    }
                }
            }

            return new
            {
                name = name,
                control_type = controlType,
                automation_id = automationId,
                bounding_rectangle = RectToObject(rect),
                is_enabled = isEnabled,
                is_offscreen = isOffscreen,
                children = children,
                _depth_limited = depthLimited,
                _truncated = false
            };
        }

        private class TreeCounter
        {
            public int Count;
            public int Limit;
            public bool Truncated;
        }

        // ── qoder 风格文本序列化（Task 10.2）──────────────

        /// <summary>
        /// 将 UIA 树序列化为 qoder 风格缩进文本格式（Task 10.2）。
        /// 第 1 行: Window: "{root.Name}", App: {ProcessName}.exe.
        /// 后续每层缩进 \t（深度 d 的节点缩进 d 个 \t）。
        /// 节点格式: {index} {control_type} [(focused)] {name} Secondary Actions: {patterns}
        /// 末尾追加空行 + The focused UI element is {index} {control_type} {name}.（若有焦点元素）。
        /// </summary>
        public string SerializeTreeToText(System.Windows.Automation.AutomationElement root,
                                           System.Windows.Automation.AutomationElement focusedElement,
                                           int maxDepth, int maxNodes)
        {
            var (text, _, _, _, _, _) = SerializeTreeToTextInternal(root, focusedElement, maxDepth, maxNodes);
            return text;
        }

        /// <summary>
        /// SerializeTreeToText 的内部实现，同时返回元素计数与截断标志供 GetUiTree 使用。
        /// 同时返回焦点元素的 index / control_type / name 供 CaptureUiSnapshot 构造 focused_element 摘要。
        /// </summary>
        private (string Text, int Count, bool Truncated, int FocusedIndex, string FocusedControlType, string FocusedName) SerializeTreeToTextInternal(
            AutomationElement root, AutomationElement? focusedElement, int maxDepth, int maxNodes)
        {
            var sb = new StringBuilder();
            var state = new TextSerializeState
            {
                Count = 0,
                Limit = maxNodes > 0 ? maxNodes : DefaultMaxNodes,
                Truncated = false,
                FocusedIndex = -1
            };

            // 第 1 行: Window 头
            string rootName = SafeName(root);
            string processName = SafeProcessName(root);
            sb.Append("Window: \"").Append(rootName).Append("\", App: ").Append(processName).Append(".\n");

            // 树节点（root 位于深度 1，缩进 1 个 \t，与 qoder 一致）
            var walker = TreeWalker.ContentViewWalker;
            try
            {
                SerializeNodeToText(sb, root, 1, maxDepth, focusedElement, walker, state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: SerializeTreeToText root failed: {ex.Message}");
            }

            // 截断标记
            if (state.Truncated)
            {
                sb.Append("\n... (truncated, ").Append(state.Count).Append(" nodes total, showing first ")
                  .Append(state.Limit).Append(")\n");
            }

            // 焦点元素尾行
            if (state.FocusedIndex >= 0)
            {
                sb.Append("\nThe focused UI element is ")
                  .Append(state.FocusedIndex).Append(' ')
                  .Append(state.FocusedControlType).Append(' ')
                  .Append(state.FocusedName).Append('.');
            }

            return (sb.ToString(), state.Count, state.Truncated,
                    state.FocusedIndex, state.FocusedControlType, state.FocusedName);
        }

        /// <summary>
        /// 递归序列化单个节点为 qoder 风格文本行。
        /// </summary>
        private void SerializeNodeToText(StringBuilder sb, AutomationElement element, int depth, int maxDepth,
                                         AutomationElement? focusedElement, TreeWalker walker,
                                         TextSerializeState state)
        {
            // 深度超过 maxDepth 停止递归（不打印、不计数）
            if (depth > maxDepth) return;

            // 节点数截断：先计数再判断，超过 Limit 则标记截断并返回
            state.Count++;
            if (state.Count > state.Limit)
            {
                state.Truncated = true;
                return;
            }

            int index = state.Count - 1;
            string controlType = SafeLocalizedControlType(element);
            string name = SafeName(element);
            string patterns = FormatPatterns(element);

            bool isFocused = false;
            try
            {
                isFocused = focusedElement != null && Automation.Compare(element, focusedElement);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: SerializeNodeToText Automation.Compare failed: {ex.Message}");
            }

            if (isFocused)
            {
                state.FocusedIndex = index;
                state.FocusedControlType = controlType;
                state.FocusedName = name;
            }

            sb.Append(new string('\t', depth));
            sb.Append(index).Append(' ').Append(controlType);
            if (isFocused) sb.Append(" (focused)");
            if (!string.IsNullOrEmpty(name)) sb.Append(' ').Append(name);
            if (!string.IsNullOrEmpty(patterns)) sb.Append(" Secondary Actions: ").Append(patterns);
            sb.Append('\n');

            // 递归子节点（depth < maxDepth 时才展开）
            if (depth < maxDepth)
            {
                AutomationElement? child = null;
                try { child = walker.GetFirstChild(element); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"UiAutomationService: SerializeNodeToText GetFirstChild failed: {ex.Message}");
                }

                while (child != null && !state.Truncated)
                {
                    AutomationElement currentChild = child;
                    try
                    {
                        SerializeNodeToText(sb, currentChild, depth + 1, maxDepth, focusedElement, walker, state);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: SerializeNodeToText child failed: {ex.Message}");
                    }

                    if (state.Truncated) break;

                    try { child = walker.GetNextSibling(currentChild); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"UiAutomationService: SerializeNodeToText GetNextSibling failed: {ex.Message}");
                        child = null;
                    }
                }
            }
        }

        private static string SafeLocalizedControlType(AutomationElement element)
        {
            try
            {
                return element.Current.ControlType?.LocalizedControlType ?? "";
            }
            catch { return ""; }
        }

        private static string SafeProcessName(AutomationElement element)
        {
            try
            {
                int pid = element.Current.ProcessId;
                using var proc = Process.GetProcessById(pid);
                // qoder 风格使用带 .exe 后缀的进程名
                return proc.ProcessName + ".exe";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: SafeProcessName failed: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 已知 UIA Pattern → 可读名称映射（Task 10.2 规范）。
        /// 其他 Pattern 取 ProgrammaticName 去 "Identifiers.Pattern" 后缀。
        /// </summary>
        private static readonly Dictionary<AutomationPattern, string> PatternNames = new Dictionary<AutomationPattern, string>
        {
            { InvokePattern.Pattern, "Invoke" },
            { TogglePattern.Pattern, "Toggle" },
            { ValuePattern.Pattern, "Value" },
            { TextPattern.Pattern, "Text" },
            { SelectionPattern.Pattern, "Selection" },
            { ScrollPattern.Pattern, "Scroll" },
            { RangeValuePattern.Pattern, "RangeValue" },
            { ExpandCollapsePattern.Pattern, "ExpandCollapse" },
            { SelectionItemPattern.Pattern, "SelectionItem" },
            { GridPattern.Pattern, "Grid" },
            { GridItemPattern.Pattern, "GridItem" },
            { TablePattern.Pattern, "Table" },
            { TableItemPattern.Pattern, "TableItem" },
            { DockPattern.Pattern, "Dock" },
            { TransformPattern.Pattern, "Transform" },
            { MultipleViewPattern.Pattern, "MultipleView" },
            { WindowPattern.Pattern, "Window" },
            { ItemContainerPattern.Pattern, "ItemContainer" },
            { VirtualizedItemPattern.Pattern, "VirtualizedItem" },
            { SynchronizedInputPattern.Pattern, "SynchronizedInput" }
        };

        /// <summary>
        /// 将元素支持的 Pattern 列表格式化为逗号分隔的可读名称字符串。
        /// 无 Pattern 时返回空字符串（调用方据此省略 Secondary Actions 段）。
        /// </summary>
        private static string FormatPatterns(AutomationElement element)
        {
            AutomationPattern[]? patterns = null;
            try { patterns = element.GetSupportedPatterns(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"UiAutomationService: FormatPatterns GetSupportedPatterns failed: {ex.Message}");
                return "";
            }
            if (patterns == null || patterns.Length == 0) return "";

            var names = new List<string>(patterns.Length);
            foreach (var p in patterns)
            {
                if (p == null) continue;
                if (PatternNames.TryGetValue(p, out var knownName))
                {
                    names.Add(knownName);
                    continue;
                }

                // 回退：从 ProgrammaticName（如 "InvokePatternIdentifiers.Pattern"）提取 "Invoke"
                string progName = "";
                try { progName = p.ProgrammaticName ?? ""; }
                catch { }
                int idx = progName.IndexOf("Identifiers");
                if (idx > 0)
                {
                    string className = progName.Substring(0, idx);
                    if (className.EndsWith("Pattern"))
                    {
                        className = className.Substring(0, className.Length - "Pattern".Length);
                    }
                    names.Add(className);
                }
                else if (!string.IsNullOrEmpty(progName))
                {
                    names.Add(progName);
                }
            }

            return string.Join(", ", names);
        }

        private class TextSerializeState
        {
            public int Count;
            public int Limit;
            public bool Truncated;
            public int FocusedIndex;
            public string FocusedControlType = "";
            public string FocusedName = "";
        }

        // ── ElementCache (private nested class) ───────────

        /// <summary>
        /// 元素缓存：ConcurrentDictionary + TTL(60s) + LRU(500)。
        /// 后台 Timer 每 30 秒清理过期条目。
        /// </summary>
        private class ElementCache
        {
            private readonly ConcurrentDictionary<string, (AutomationElement Element, DateTime ExpiresAt, DateTime LastAccess)> _store
                = new ConcurrentDictionary<string, (AutomationElement, DateTime, DateTime)>();
            private readonly Timer _cleanupTimer;
            private const int TtlSeconds = 60;
            private const int LruCap = 500;

            public ElementCache()
            {
                _cleanupTimer = new Timer(CleanupCallback, null,
                    TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }

            public void Store(string id, AutomationElement element)
            {
                var now = DateTime.UtcNow;
                _store[id] = (element, now.AddSeconds(TtlSeconds), now);
                if (_store.Count > LruCap)
                {
                    EvictOldest();
                }
            }

            public AutomationElement? Get(string id)
            {
                if (_store.TryGetValue(id, out var entry))
                {
                    var now = DateTime.UtcNow;
                    if (now > entry.ExpiresAt)
                    {
                        _store.TryRemove(id, out _);
                        return null;
                    }
                    // 更新 LastAccess（LRU）
                    _store[id] = (entry.Element, entry.ExpiresAt, now);
                    return entry.Element;
                }
                return null;
            }

            private void EvictOldest()
            {
                string? oldestKey = null;
                DateTime oldestTime = DateTime.MaxValue;
                foreach (var kvp in _store)
                {
                    if (kvp.Value.LastAccess < oldestTime)
                    {
                        oldestTime = kvp.Value.LastAccess;
                        oldestKey = kvp.Key;
                    }
                }
                if (oldestKey != null)
                {
                    _store.TryRemove(oldestKey, out _);
                }
            }

            private void CleanupCallback(object? state)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    foreach (var kvp in _store)
                    {
                        if (now > kvp.Value.ExpiresAt)
                        {
                            _store.TryRemove(kvp.Key, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ElementCache cleanup failed: {ex.Message}");
                }
            }

            public string GenerateElementId()
            {
                string id;
                do
                {
                    id = "el-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                } while (_store.ContainsKey(id));
                return id;
            }
        }

        // ── P/Invoke (EnumWindows 相关) ───────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        // 64 位兼容包装：IntPtr.Size == 8 时调用 GetWindowLongPtr，否则 GetWindowLong
        private static IntPtr GetWindowLongCompat(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
    }

    /// <summary>
    /// 顶层窗口信息（GetWindowSnapshot 返回）。
    /// </summary>
    public class WindowInfo
    {
        public string Title { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public int ProcessId { get; set; }
        public IntPtr Hwnd { get; set; }
        public bool IsVisible { get; set; }
    }
}
