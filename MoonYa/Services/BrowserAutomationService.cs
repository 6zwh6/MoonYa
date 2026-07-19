// BrowserAutomationService — 基于 PuppeteerSharp 的浏览器操控引擎
// 独立于 CU 组件（不调用 ComputerUseService / UiAutomationService / Graphics.CopyFromScreen）
// 失败直接抛异常，不降级到坐标点击 / 键盘 / CU 兜底；不自动重试

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace MoonYa.Services
{
    /// <summary>浏览器自动化配置（由调用方从 launcher_config.json 的 browser_automation 段读取后注入）</summary>
    public class BrowserAutomationConfig
    {
        public int Port { get; set; } = 58905;
        public System.Collections.Generic.List<string> TrustedDomains { get; set; } = new();
        public string ChromiumExecutablePath { get; set; } = string.Empty;
        public bool Headless { get; set; } = false;
        public bool AutoDownloadChromium { get; set; } = true;
        public int DefaultTimeoutMs { get; set; } = 30000;
        public int ElementTimeoutMs { get; set; } = 8000;
        public int ViewportWidth { get; set; } = 1280;
        public int ViewportHeight { get; set; } = 800;
    }

    public class BrowserAutomationService : IDisposable
    {
        // 页面加载等待参数：networkidle + DOM 稳定组合策略（Playwright 标准）
        private const int ClickInitialDelayMs = 300;    // click 后让事件传播和动画开始的初始延迟
        private const int NetworkIdleTimeoutMs = 6000;  // 网络空闲等待最大时长
        private const int NetworkIdleTimeMs = 500;      // 500ms 内无网络请求视为空闲（Playwright 默认值）
        private const int DomStableMaxWaitMs = 6000;    // 等待 DOM 稳定的最大总时长
        private const int DomStableThresholdMs = 1500;  // 元素数量保持不变持续时长阈值（1.5s，适应 Vue/React 动态渲染）
        private const int DomStablePollMs = 200;        // DOM 轮询间隔

        // 每次采集返回的元素上限（单 document）：宝塔/ElementUI/LayUI/Vue 后台 DOM 往往 1500~3000+，
        // 原 500 会截断弹窗内元素；提升至 2000 配合 modal 优先排序，确保弹窗元素不被截断
        private const int PerDocumentElementLimit = 2000;
        // 最终返回给调用方的元素上限（弹窗内 > iframe 内 > 普通元素 排序后截取）
        private const int MaxReturnedElements = 120;

        private readonly BrowserAutomationConfig _config;
        private IBrowser? _browser;
        private IPage? _page;
        private bool _disposed;
        // 记录 BA 启动前已存在的 chrome 进程 PID，用于退出时精准清理本次启动的 chromium 子进程
        private readonly HashSet<int> _preLaunchChromePids = new();

        // ★ 跨 frame 元素采集脚本：在主 document 和每个 iframe 内执行
        //   注意：不再遍历 iframe.contentDocument（同源限制），改为由 C# 端调用 page.Frames 分别执行
        private static readonly string CollectElementsJs = @"() => {
            const elements = [];
            // ★ 扩展交互元素选择器：覆盖 Element UI / Layui / Ant Design / Bootstrap 等框架封装的组件
            //   大量按钮实际是 <div>/<span> + class（layui-layer-btn0/btlink/confirm/submit 等），
            //   必须扩展 onclick / tabindex / class*=confirm|submit|ok|cancel|close / cursor:pointer 等
            const selectors = [
                'input', 'button', 'a', 'select', 'textarea', 'label',
                'div[onclick]', 'span[onclick]', 'i[onclick]',
                '[tabindex]',
                '[role=""button""]', '[role=""link""]', '[role=""checkbox""]', '[role=""tab""]',
                '[contenteditable=""true""]',
                '.el-input__inner', '.el-textarea__inner', '.el-button', '.el-select', '.el-cascader',
                '.layui-btn', '.layui-form-select', '.layui-layer-btn0', '.layui-layer-btn1', '.layui-layer-btn2', '.btlink',
                '.ant-btn', '.ant-input', '.ant-select-selector',
                '[class*=""btn""]', '[class*=""button""]', '[class*=""input""]',
                '[class*=""confirm""]', '[class*=""submit""]', '[class*=""ok""]',
                '[class*=""cancel""]', '[class*=""close""]',
                // ★ 下拉/上下文菜单项：v-contextmenu 插件（宝塔文件管理「更多」菜单）、Element UI dropdown/select
                '.v-contextmenu-item', '.el-dropdown-menu__item', '.el-select-dropdown__item',
                '[style*=""cursor: pointer""]', '[style*=""cursor:pointer""]'
            ].join(',');
            // ★ 弹窗检测选择器扩展：覆盖 Element UI / Layui / Ant Design / Bootstrap / 自定义弹窗 / 上下文菜单 / 下拉菜单
            //   v-contextmenu: 宝塔文件管理「更多」按钮弹出的菜单（position:absolute zIndex:2020）
            //   el-dropdown-menu / el-select-dropdown: Element UI 下拉菜单
            const modalSelectors = '[role=""dialog""], .modal, .modal-dialog, .modal-content, .el-dialog, .el-dialog__wrapper, .v-modal, .el-message-box, .el-drawer, .ant-modal, .ant-modal-root, .ant-modal-wrap, .ant-modal-content, .ant-drawer, .layui-layer, .layui-layer-page, .layui-layer-content, .layui-layer-main, .ivu-modal, .ivu-modal-wrap, .bt-form, .bt-modal, .bt-popup, .v-contextmenu, .el-dropdown-menu, .el-select-dropdown, [class*=""modal""], [class*=""dialog""], [class*=""popup""], [class*=""layer""], [class*=""drawer""], [class*=""contextmenu""], [class*=""context-menu""]';
            // ★ 注意：已移除 .el-popup-parent--hidden —— 它是 Element UI 加到 body 上的辅助 class（防背景滚动），
            //   不是弹窗容器；保留它会导致 body 匹配，所有元素的 closest() 都返回 body，全部被误标 in_modal=true
            // ★ 判断元素是否可见
            function isVisible(el) {
                if (!el) return false;
                const rect = el.getBoundingClientRect();
                if (rect.width === 0 || rect.height === 0) return false;
                const style = (el.ownerDocument && el.ownerDocument.defaultView)
                    ? el.ownerDocument.defaultView.getComputedStyle(el)
                    : window.getComputedStyle(el);
                return !(style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0');
            }

            // ★ 判断元素是否在弹窗/浮层内
            function isInModal(el, doc) {
                // ★ closest 匹配需排除 body/html —— Element UI 弹窗打开时给 body 加 el-popup-parent--hidden，
                //   [class*=""popup""] 仍会匹配 body，导致所有元素 closest() 返回 body 被误标 in_modal=true
                const modalMatch = el.closest(modalSelectors);
                if (modalMatch && modalMatch !== doc.body && modalMatch !== doc.documentElement) return true;
                // ★ z-index 补充检查：祖先有 position=fixed/absolute + zIndex>50 时，
                //   还需该祖先匹配 modalSelectors 才认定为弹窗 ——
                //   避免导航栏（如宝塔 .layout-left-menu position:absolute zIndex:9999）被误判为弹窗
                let node = el;
                while (node && node !== doc.body) {
                    try {
                        const nodeStyle = (node.ownerDocument && node.ownerDocument.defaultView)
                            ? node.ownerDocument.defaultView.getComputedStyle(node)
                            : window.getComputedStyle(node);
                        if (nodeStyle && (nodeStyle.position === 'fixed' || nodeStyle.position === 'absolute')) {
                            const z = parseInt(nodeStyle.zIndex || '0', 10);
                            if (!isNaN(z) && z > 50) {
                                try { if (node.matches(modalSelectors)) return true; } catch (e) { }
                            }
                        }
                    } catch (e) { }
                    node = node.parentElement;
                }
                return false;
            }

            // ★ 过滤框架生成的动态 ID
            function isStableId(id) {
                if (!id) return false;
                if (/^el-id-\d+-\d+$/.test(id)) return false;
                if (/^el-\d+$/.test(id)) return false;
                if (/^app-\d+$/.test(id)) return false;
                if (/^\d+$/.test(id)) return false;
                if (id.length < 2) return false;
                return true;
            }

            // ★ 生成 CSS 选择器：优先稳定可靠的属性
            function buildSelector(el) {
                const tagLower = el.tagName.toLowerCase();
                // 表单元素优先 name 属性
                if ((tagLower === 'input' || tagLower === 'textarea' || tagLower === 'select') && el.name) {
                    return tagLower + '[name=""' + CSS.escape(el.name) + '""]';                }
                if (isStableId(el.id)) {
                    return '#' + CSS.escape(el.id);
                }
                if (el.name) {
                    return tagLower + '[name=""' + CSS.escape(el.name) + '""]';                }
                if (el.getAttribute('data-testid')) {
                    return '[data-testid=""' + CSS.escape(el.getAttribute('data-testid')) + '""]';                }
                if (el.placeholder && (tagLower === 'input' || tagLower === 'textarea')) {
                    return tagLower + '[placeholder=""' + CSS.escape(el.placeholder) + '""]';                }
                // ★ 对 Element UI 等框架组件，用 class 组合更可靠
                if (el.className && typeof el.className === 'string' && el.className.trim()) {
                    const classes = el.className.trim().split(/\s+/);
                    // 过滤掉框架工具类（如 is-active、is-focus 等状态类）
                    const meaningful = classes.filter(c =>
                        c.length >= 2 &&
                        !/^is-/.test(c) &&
                        !/^el-icon/.test(c) &&
                        !/^fa-/.test(c) &&
                        !/^icon-/.test(c)
                    ).slice(0, 2);
                    if (meaningful.length > 0) {
                        return tagLower + meaningful.map(c => '.' + CSS.escape(c)).join('');
                    }
                }
                // ★ 兜底：用父元素+标签路径
                const parent = el.parentNode;
                if (parent) {
                    const siblings = Array.from(parent.children).filter(s => s.tagName === el.tagName);
                    if (siblings.length > 1) {
                        return tagLower + ':nth-of-type(' + (siblings.indexOf(el) + 1) + ')';
                    }
                }
                return tagLower;
            }

            // ★ 唯一性验证：生成 selector 后若 querySelectorAll().length > 1，
            //   逐级添加父元素路径 + :nth-of-type() 直到唯一（最多 5 级，避免选择器过长）
            //   这是修复“点击错误元素”的核心：多个 button.el-button 时 querySelector 只命中第一个
            function makeUnique(el, baseSelector, doc) {
                try {
                    if (doc.querySelectorAll(baseSelector).length === 1) return baseSelector;
                } catch (e) { return baseSelector; }

                // ★ 先对目标元素自身加 :nth-of-type(N)，区分同标签兄弟
                //   仅靠祖先路径无法区分共用同一父元素的同标签兄弟：3 个 button.auth-tab-btn 共用 .auth-tabs，
                //   加多少层祖先 selector 仍匹配全部 3 个，querySelector 只返回第一个（错误元素）
                let effectiveBase = baseSelector;
                const selfParent = el.parentElement;
                if (selfParent) {
                    const selfSiblings = Array.from(selfParent.children).filter(s => s.tagName === el.tagName);
                    if (selfSiblings.length > 1) {
                        const selfIdx = selfSiblings.indexOf(el);
                        if (selfIdx >= 0) {
                            effectiveBase = baseSelector + ':nth-of-type(' + (selfIdx + 1) + ')';
                            try {
                                if (doc.querySelectorAll(effectiveBase).length === 1) return effectiveBase;
                            } catch (e) { /* nth-of-type 不适用，回退到原 base 继续加祖先 */ }
                        }
                    }
                }

                const parts = [effectiveBase];
                let node = el.parentElement;
                let depth = 0;
                const maxDepth = 5;

                while (node && depth < maxDepth) {
                    depth++;
                    const parentTag = node.tagName ? node.tagName.toLowerCase() : '';
                    if (!parentTag || parentTag === 'html' || parentTag === 'body') break;

                    // 父元素在同标签兄弟中的位置
                    const grandParent = node.parentElement;
                    let parentPart;
                    if (grandParent) {
                        const parentSiblings = Array.from(grandParent.children).filter(s => s.tagName === node.tagName);
                        const parentIdx = parentSiblings.indexOf(node);
                        if (parentSiblings.length > 1 && parentIdx >= 0) {
                            parentPart = parentTag + ':nth-of-type(' + (parentIdx + 1) + ')';
                        } else {
                            parentPart = parentTag;
                        }
                    } else {
                        parentPart = parentTag;
                    }

                    parts.unshift(parentPart);
                    const candidate = parts.join(' > ');
                    try {
                        if (doc.querySelectorAll(candidate).length === 1) return candidate;
                    } catch (e) { break; }
                    node = node.parentElement;
                }
                // 仍未唯一，返回最深路径（比原 base 更精确）
                return parts.join(' > ');
            }

            function collectFromDoc(doc) {
                const nodes = doc.querySelectorAll(selectors);
                // ★ 两遍收集：先弹窗内元素，后弹窗外元素
                const modalEls = [];
                const otherEls = [];
                nodes.forEach((el) => {
                    if (!isVisible(el)) return;
                    if (isInModal(el, doc)) {
                        modalEls.push(el);
                    } else {
                        otherEls.push(el);
                    }
                });

                // 处理单个元素
                function processEl(el) {
                    if (elements.length >= " + PerDocumentElementLimit + @") return;
                    const rect = el.getBoundingClientRect();
                    const tagLower = el.tagName.toLowerCase();
                    let cssSelector = buildSelector(el);

                    // ★ 对 <a> 标签，用 pathname 关键词增强选择器
                    if (tagLower === 'a' && el.href && el.href !== '#' && !el.href.startsWith('javascript:')) {
                        try {
                            var hrefPath = new URL(el.href).pathname;
                            var seg = hrefPath.split('/').filter(function(s) { return s.length >= 2; })[0];
                            if (seg) {
                                cssSelector += '[href*=""' + seg + '""]';
                            }
                        } catch (e) { }
                    }

                    // ★ 唯一性验证：确保生成的 selector 在当前 doc 内仅命中本元素
                    //   多个 button.el-button 等场景下，querySelector 只命中第一个导致点击错误
                    cssSelector = makeUnique(el, cssSelector, doc);

                    // ★ 提取元素文本：优先 innerText，回退到 value/placeholder/aria-label/title/关联 label
                    let text = '';
                    if (el.innerText && el.innerText.trim()) {
                        text = el.innerText.trim();
                    } else if (el.textContent && el.textContent.trim()) {
                        text = el.textContent.trim();
                    } else {
                        text = (el.value || el.placeholder || el.getAttribute('aria-label') || el.title || '').trim();
                    }
                    // 如果是输入框且没有文本，尝试找关联 label
                    if (!text && (tagLower === 'input' || tagLower === 'textarea' || tagLower === 'select')) {
                        // aria-labelledby
                        const labelledBy = el.getAttribute('aria-labelledby');
                        if (labelledBy) {
                            const labelEl = doc.getElementById(labelledBy);
                            if (labelEl) text = (labelEl.innerText || labelEl.textContent || '').trim();
                        }
                        // 找 for=id 的 label
                        if (!text && el.id) {
                            const labels = doc.querySelectorAll('label[for=""' + CSS.escape(el.id) + '""]');
                            if (labels.length > 0) text = (labels[0].innerText || labels[0].textContent || '').trim();
                        }
                        // 找相邻 label
                        if (!text) {
                            const prevLabel = el.parentElement && el.parentElement.tagName.toLowerCase() === 'label' ? el.parentElement : null;
                            if (prevLabel) text = (prevLabel.innerText || prevLabel.textContent || '').trim();
                        }
                    }

                    elements.push({
                        tag: tagLower,
                        type: el.type || el.getAttribute('role') || '',
                        css_selector: cssSelector,
                        text: text.slice(0, 120),
                        name: el.name || '',
                        id: el.id || '',
                        placeholder: el.placeholder || '',
                        href: el.href || '',
                        value: (el.value || '').toString().slice(0, 80),
                        position: { x: Math.round(rect.x), y: Math.round(rect.y), w: Math.round(rect.width), h: Math.round(rect.height) },
                        disabled: el.disabled || false,
                        visible: true,
                        in_modal: isInModal(el, doc),
                        in_iframe: false
                    });
                }
                // 先处理弹窗内元素（确保不被数量限制截断）
                modalEls.forEach(processEl);
                // 再处理弹窗外元素
                otherEls.forEach(processEl);
            }

            // 主文档
            collectFromDoc(document);

            // 穿透 shadow DOM
            try {
                function walkShadow(node) {
                    if (!node || !node.querySelectorAll) return;
                    node.querySelectorAll('*').forEach((el) => {
                        if (el.shadowRoot) {
                            try {
                                collectFromDoc(el.shadowRoot);
                                walkShadow(el.shadowRoot);
                            } catch (e) { }
                        }
                    });
                }
                walkShadow(document);
            } catch (e) { }

            // ★ 兜底：从主文档 JS 直接访问同源 iframe 的 contentDocument
            //    C# 端虽有 _page.Frames 遍历，但某些场景下 iframe 可能不在 Frames 集合中
            //    或 frames 遍历时 iframe 内容尚未就绪。此处作为兜底，确保同源 iframe 内元素必被捕获
            try {
                var iframes = document.querySelectorAll('iframe, frame');
                iframes.forEach(function(ifr) {
                    try {
                        var ifrDoc = ifr.contentDocument || (ifr.contentWindow ? ifr.contentWindow.document : null);
                        if (ifrDoc && ifrDoc !== document && ifrDoc.body && ifrDoc.body.querySelectorAll) {
                            collectFromDoc(ifrDoc);
                        }
                    } catch (e) { }
                });
            } catch (e) { }

            // 提取全页面可见文本
            const pageText = (function() {
                try {
                    const body = document.body;
                    if (!body) return '';
                    const results = [];
                    const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT, {
                        acceptNode: function(node) {
                            if (!node.parentElement) return NodeFilter.FILTER_REJECT;
                            const tag = node.parentElement.tagName || '';
                            if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'SVG' || tag === 'IFRAME') return NodeFilter.FILTER_REJECT;
                            const style = window.getComputedStyle(node.parentElement);
                            if (style.display === 'none' || style.visibility === 'hidden') return NodeFilter.FILTER_REJECT;
                            const text = node.textContent.trim();
                            if (!text) return NodeFilter.FILTER_REJECT;
                            return NodeFilter.FILTER_ACCEPT;
                        }
                    }, false);
                    let node;
                    while ((node = walker.nextNode())) {
                        const t = node.textContent.trim();
                        if (t) results.push(t);
                    }
                    const unique = [];
                    for (const r of results) {
                        if (unique.length === 0 || r !== unique[unique.length - 1]) unique.push(r);
                    }
                    return unique.join(' ').replace(/\s{2,}/g, ' ').slice(0, 4000);
                } catch (e) { return ''; }
            })();

            return JSON.stringify({elements: elements, page_text: pageText});
        }";

        // ★ :has-text() Playwright 语法在 PuppeteerSharp 中不支持，转换为真实 CSS 选择器
        private static readonly string HasTextResolveJs = @"(tagSel, searchText) => {
            const nodes = document.querySelectorAll(tagSel);
            for (const el of nodes) {
                const t = (el.innerText || el.textContent || '').trim();
                if (t.includes(searchText)) {
                    const rect = el.getBoundingClientRect();
                    if (rect.width > 0 && rect.height > 0) {
                        if (el.id) return '#' + CSS.escape(el.id);
                        const tagN = el.tagName.toLowerCase();
                        const parent = el.parentElement;
                        if (parent) {
                            const siblings = Array.from(parent.querySelectorAll(':scope > ' + tagN));
                            const idx = siblings.indexOf(el);
                            if (idx >= 0 && siblings.length > 1) return tagN + ':nth-of-type(' + (idx + 1) + ')';
                        }
                        return tagN;
                    }
                }
            }
            return '';
        }";

        // ★ 可见可交互元素存在性检测：用于 WaitForFunctionAsync，等待真正可见的交互元素出现
        //   修复原 bug：仅检查 querySelectorAll().length > 0 会把 display:none 的元素也算上，
        //   导致 Vue/弹窗尚未渲染完成就提前采集，返回空 elements
        //   新条件：至少存在一个 rect.width>0 && rect.height>0 && display!=none && visibility!=hidden 的元素
        private static readonly string VisibleInteractiveCheckJs = @"() => {
            return Array.from(document.querySelectorAll('input,button,textarea,select,a,[role=""button""],div[onclick],span[onclick],[class*=""btn""],[class*=""confirm""],[class*=""submit""],[class*=""ok""],[class*=""cancel""],[class*=""close""]')).some(function(el){
                var rect = el.getBoundingClientRect();
                if (rect.width <= 0 || rect.height <= 0) return false;
                var cs = getComputedStyle(el);
                return cs.display !== 'none' && cs.visibility !== 'hidden';
            });
        }";

        public BrowserAutomationService(BrowserAutomationConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // 启动有头 Chromium；如未指定 ExecutablePath 且开启 AutoDownloadChromium，调用 BrowserFetcher 下载
        public async Task<bool> StartAsync(string url)
        {
            if (_browser != null && !_browser.IsClosed) return true;

            var launchOpts = new LaunchOptions
            {
                Headless = _config.Headless,
                AcceptInsecureCerts = true,
                DefaultViewport = new ViewPortOptions
                {
                    Width = _config.ViewportWidth,
                    Height = _config.ViewportHeight,
                },
            };

            if (!string.IsNullOrEmpty(_config.ChromiumExecutablePath))
            {
                launchOpts.ExecutablePath = _config.ChromiumExecutablePath;
            }
            else if (_config.AutoDownloadChromium)
            {
                // 不指定 ExecutablePath 时，PuppeteerSharp 自身不会自动下载，需显式触发
                // PuppeteerSharp 20.0 中 BrowserFetcher 不再实现 IDisposable，使用工厂方法创建实例
                var browserFetcher = Puppeteer.CreateBrowserFetcher(new BrowserFetcherOptions());
                await browserFetcher.DownloadAsync();
            }

            // 记录本次 BA 启动前已存在的 chrome 进程，避免退出时误杀用户自己的 Chrome
            _preLaunchChromePids.Clear();
            try
            {
                foreach (var p in Process.GetProcessesByName("chrome"))
                    _preLaunchChromePids.Add(p.Id);
            }
            catch { }

            _browser = await Puppeteer.LaunchAsync(launchOpts);
            _page = await _browser.NewPageAsync();
            _page.DefaultTimeout = _config.DefaultTimeoutMs;
            _page.DefaultNavigationTimeout = _config.DefaultTimeoutMs;

            await _page.GoToAsync(url, new NavigationOptions
            {
                Timeout = _config.DefaultTimeoutMs,
                WaitUntil = new[] { WaitUntilNavigation.Load },
            });
            // ★ 组合等待：networkidle + DOM 稳定（初始页面也有 AJAX 异步渲染）
            await WaitForNetworkIdleSafeAsync();
            await WaitForDomStableAsync();

            return true;
        }

        // 导航到新 url（超时由 default_timeout_ms 控制）
        public async Task<bool> NavigateAsync(string url)
        {
            EnsureStarted();
            await _page!.GoToAsync(url, new NavigationOptions
            {
                Timeout = _config.DefaultTimeoutMs,
                WaitUntil = new[] { WaitUntilNavigation.Load },
            });
            // ★ 组合等待：networkidle + DOM 稳定
            //   Load 事件后 AJAX 框架（Vue/React）仍会异步渲染，需等网络空闲 + DOM 稳定
            await WaitForNetworkIdleSafeAsync();
            await WaitForDomStableAsync();
            return true;
        }

        // 点击 CSS 选择器元素；元素未找到抛 InvalidOperationException
        public async Task ClickAsync(string selector)
        {
            EnsureStarted();
            var element = await WaitForSelectorSafe(selector);
            await element.ClickAsync();
            // ★ 组合等待策略（Playwright 标准）：networkidle + DOM 稳定
            //   1. 初始延迟：让点击事件传播和动画开始
            //   2. networkidle：等待 AJAX 请求完成（弹窗表单异步加载的根因）
            //   3. MutationObserver DOM 稳定：监听真实 mutation，300ms 无变化视为渲染完成（比轮询更精准）
            //   4. 轮询 DOM 稳定：兜底确认元素数量稳定
            //   不用 WaitForNavigationAsync：SPA pushState 不触发 navigation 事件
            await Task.Delay(ClickInitialDelayMs);
            await WaitForNetworkIdleSafeAsync();
            await WaitForDomStableMutationAsync();
            await WaitForDomStableAsync();
            // ★ 弹窗内容 settled 等待：若点击后页面存在弹窗/浮层，额外等待让表单/iframe 渲染完成
            await WaitForModalContentAsync();
        }

        // 输入文本：直接通过 JS 设置 value 并触发 input 事件（比逐字符 TypeAsync 快 10 倍+）
        public async Task FillAsync(string selector, string text)
        {
            EnsureStarted();
            var element = await WaitForSelectorSafe(selector);
            await element.FocusAsync();

            // 通过 JS 直接设置 value + 派发 input 事件，确保前端框架（Vue/React）能监听到变化
            await element.EvaluateFunctionAsync(@"(el, val) => {
                el.value = val;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            }", text ?? string.Empty);
        }

        // 核心方法：仅截视口（FullPage=false），返回 base64 字符串供调用方包装为 data URL
        public async Task<string> ScreenshotBase64Async()
        {
            EnsureStarted();
            // PuppeteerSharp 20.0 中 ScreenshotAsync 仅支持写入文件路径；如需 byte[] 须改用 ScreenshotDataAsync
            var bytes = await _page!.ScreenshotDataAsync(new ScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = false,
            });
            return Convert.ToBase64String(bytes);
        }

        // 通过 window.scrollBy 实现 up/down 滚动，避免依赖元素滚动 API
        public async Task ScrollAsync(string direction, int amount)
        {
            EnsureStarted();
            var dir = (direction ?? string.Empty).ToLowerInvariant();
            int dy = dir == "up" ? -Math.Abs(amount) : Math.Abs(amount);
            await _page!.EvaluateExpressionAsync($"window.scrollBy(0, {dy})");
            // ★ 组合等待：networkidle + DOM 稳定（懒加载可能触发 AJAX + IntersectionObserver 渲染）
            await WaitForNetworkIdleSafeAsync();
            await WaitForDomStableAsync();
        }

        // 获取元素 innerText（未找到抛 InvalidOperationException）
        public async Task<string> GetTextAsync(string selector)
        {
            EnsureStarted();
            var element = await WaitForSelectorSafe(selector);
            var text = await element.EvaluateFunctionAsync<string>("el => el.innerText");
            return text ?? string.Empty;
        }

        // 获取页面所有可交互元素的 DOM 信息（100% 准确的 CSS 选择器，不依赖视觉猜测）
        // ★ 关键改进：遍历 page.Frames，采集跨域 iframe 内元素，标记 frame_url/frame_name；
        //   跨域/未就绪 iframe 兜底：提取 iframe.src 在新页面中采集
        public async Task<string> GetElementsAsync()
        {
            EnsureStarted();
            var allElements = new List<Dictionary<string, object>>();
            var pageTextParts = new List<string>();
            var diagLog = new List<string>();  // 诊断日志

            // 0. 从主文档提取所有 <iframe> 的 src/id（用于诊断 + 兜底）
            var iframesInDom = new List<(string src, string id)>();
            try
            {
                var iframeJson = await _page!.EvaluateExpressionAsync<string>(@"
                    JSON.stringify(Array.from(document.querySelectorAll('iframe, frame')).map(function(f) {
                        return { src: f.src || '', id: f.id || '' };
                    }))
                ");
                if (!string.IsNullOrWhiteSpace(iframeJson) && iframeJson != "null" && iframeJson != "[]")
                {
                    var parsed = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(iframeJson);
                    if (parsed != null)
                    {
                        foreach (var p in parsed)
                        {
                            p.TryGetValue("src", out var src);
                            p.TryGetValue("id", out var id);
                            if (!string.IsNullOrWhiteSpace(src) && src != "about:blank")
                                iframesInDom.Add((src ?? "", id ?? ""));
                        }
                    }
                }
            }
            catch (Exception) { }

            // 1. 主 frame
            var mainResult = await _page!.EvaluateFunctionAsync<string>(CollectElementsJs);
            ParseElementsResult(mainResult, allElements, pageTextParts, isFrame: false, frameUrl: string.Empty, frameName: string.Empty);

            // 2. 检测是否有子 frame（基于 page.Frames，而非主文档 querySelectorAll）
            //    原因：iframe 可能在 shadow DOM 或弹窗内，主文档 querySelectorAll 找不到
            //    但 PuppeteerSharp 的 page.Frames 能通过浏览器内部 API 获取所有 frame
            bool hasChildFrames = _page.Frames.Any(f => f != _page.MainFrame);

            // ★ 2a. 等待 Frame 数量稳定：宝塔弹窗点击后 iframe 延迟创建（100~500ms），
            //   若 GetElementsAsync 早于 iframe 创建，整个弹窗不会被采集。
            //   策略：轮询 frame 数量，连续 300ms 无变化视为稳定（最多等 2 秒）
            int lastFrameCount = _page.Frames.Length;
            var frameStableSince = DateTime.UtcNow;
            var frameStableDeadline = DateTime.UtcNow.AddMilliseconds(2000);
            while (DateTime.UtcNow < frameStableDeadline)
            {
                await Task.Delay(200);
                int nowFrameCount;
                try { nowFrameCount = _page.Frames.Length; }
                catch (Exception) { continue; }
                if (nowFrameCount == lastFrameCount)
                {
                    if ((DateTime.UtcNow - frameStableSince).TotalMilliseconds >= 300) break;
                }
                else
                {
                    diagLog.Add($"FrameStabilize: FrameCount {lastFrameCount} -> {nowFrameCount}, waiting for render");
                    lastFrameCount = nowFrameCount;
                    frameStableSince = DateTime.UtcNow;
                    // 新 frame 出现后等待 200ms 让其渲染
                    await Task.Delay(200);
                }
            }
            hasChildFrames = _page.Frames.Any(f => f != _page.MainFrame);

            // 3. 所有子 frame — 重试收集
            int frameRetryCount = 0;
            const int maxFrameRetries = 2;  // ★ 缩减为 2 次：宝塔弹窗 1-2 次重试足够，原 5 次过度
            bool collectedFromAnyFrame = false;

            // ★ 记录哪些 frame URL 已经成功采集到非空元素（诊断用）
            var collectedFrameUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int prevFrameCount = _page.Frames.Length;

            do
            {
                frameRetryCount++;
                int framesBefore = allElements.Count(e => GetBool(e, "in_frame"));
                int curFrameCount = _page.Frames.Length;

                // ★ 若 frame 数量增加，等待 300ms 让新 frame 内容渲染（覆盖延迟创建的 iframe）
                if (curFrameCount > prevFrameCount)
                {
                    diagLog.Add($"Retry#{frameRetryCount}: FrameCount increased {prevFrameCount} -> {curFrameCount}, wait 300ms");
                    await Task.Delay(300);
                }
                prevFrameCount = curFrameCount;

                foreach (var frame in _page.Frames)
                {
                    if (frame == _page.MainFrame) continue;
                    var frameUrl = frame.Url ?? string.Empty;
                    try
                    {
                        // ★ 等待 frame 内有可见交互元素就绪，最多 2 秒
                        //   宝塔弹窗 iframe 是 about:blank 动态创建，内容异步渲染；
                        //   不等待直接采集会返回空 elements 数组
                        try
                        {
                            // ★ 等待真正可见的交互元素出现（而非仅 querySelectorAll 命中 display:none 元素）
                            await frame.WaitForFunctionAsync(
                                VisibleInteractiveCheckJs,
                                new WaitForFunctionOptions { Timeout = 2000, PollingInterval = 200 }
                            );
                        }
                        catch (Exception)
                        {
                            // 超时不阻断，继续尝试采集（可能 frame 内确实无交互元素）
                        }

                        int beforeCount = allElements.Count;
                        var frameResult = await frame.EvaluateFunctionAsync<string>(CollectElementsJs);
                        if (!string.IsNullOrWhiteSpace(frameResult) && frameResult != "null")
                        {
#pragma warning disable CS0618
                            var frameName = frame.Name ?? string.Empty;
#pragma warning restore CS0618
                            ParseElementsResult(frameResult, allElements, pageTextParts, isFrame: true, frameUrl: frameUrl, frameName: frameName);
                            // ★ 只有真正采集到元素才标记 collectedFromAnyFrame
                            //   修复原 bug：frame 返回空 elements 数组时也标记 true，导致重试被跳过
                            if (allElements.Count > beforeCount)
                            {
                                collectedFromAnyFrame = true;
                            }
                            if (!string.IsNullOrWhiteSpace(frameUrl))
                                collectedFrameUrls.Add(frameUrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        var errMsg = $"Frame[{frameUrl}] attempt#{frameRetryCount}: {ex.GetType().Name}: {ex.Message}";
                        System.Diagnostics.Debug.WriteLine(errMsg);
                        diagLog.Add(errMsg);
                    }
                }

                int framesAfter = allElements.Count(e => GetBool(e, "in_frame"));
                // ★ 重试终止条件：
                //   1. framesAfter > framesBefore：本帧采集到 in_frame 元素，停止
                //   2. !hasChildFrames：没有子 frame，停止
                if (framesAfter > framesBefore) break;
                if (!hasChildFrames) break;

                if (frameRetryCount < maxFrameRetries)
                {
                    await Task.Delay(800);
                    try { await WaitForNetworkIdleSafeAsync(); await WaitForDomStableMutationAsync(); } catch (Exception) { }
                }
            }
            while (frameRetryCount < maxFrameRetries);

            // ★ 3a. 最终 frame 变化检查：retry 循环结束后，若 frame 数量仍比循环中多，
            //   再做一次采集（兜底延迟创建的 frame，宝塔点击→延迟→创建iframe→Vue渲染场景）
            try
            {
                int finalFrameCount = _page.Frames.Length;
                if (finalFrameCount > prevFrameCount)
                {
                    diagLog.Add($"PostLoop: FrameCount increased {prevFrameCount} -> {finalFrameCount}, re-collecting");
                    await Task.Delay(300);
                    foreach (var frame in _page.Frames)
                    {
                        if (frame == _page.MainFrame) continue;
                        var frameUrl = frame.Url ?? string.Empty;
                        try
                        {
                            try
                            {
                                await frame.WaitForFunctionAsync(
                                    VisibleInteractiveCheckJs,
                                    new WaitForFunctionOptions { Timeout = 3000, PollingInterval = 200 }
                                );
                            }
                            catch (Exception) { }
                            int beforeCount = allElements.Count;
                            var frameResult = await frame.EvaluateFunctionAsync<string>(CollectElementsJs);
                            if (!string.IsNullOrWhiteSpace(frameResult) && frameResult != "null")
                            {
#pragma warning disable CS0618
                                var frameName = frame.Name ?? string.Empty;
#pragma warning restore CS0618
                                ParseElementsResult(frameResult, allElements, pageTextParts, isFrame: true, frameUrl: frameUrl, frameName: frameName);
                                if (allElements.Count > beforeCount && !string.IsNullOrWhiteSpace(frameUrl))
                                    collectedFrameUrls.Add(frameUrl);
                            }
                        }
                        catch (Exception ex)
                        {
                            diagLog.Add($"PostLoop Frame[{frameUrl}]: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception) { }

            // ★ 4. 跨域 iframe 兜底已移除（原 L729-786）
            //   原因：兜底在新 page 采集后立即释放（using var newPage），css_selector 属于新 page 上下文，
            //         ClickAsync→WaitForSelectorSafe 只搜 _page.MainFrame + _page.Frames，无法搜已释放的新 page，
            //         导致兜底采集的元素 100% 无法 click，反而误导 AI 反复点击失败。
            //         且每个跨域 iframe 兜底耗时 20s（15s GoTo + 5s networkidle），严重拖慢速度。
            //   正确做法：跨域 iframe 内容由 AI 调用 vls_analyze_browser 获取视觉描述（仅用文字理解，不用其 css_selector）。
            if (iframesInDom.Count > 0)
            {
                var uncapturedIframes = iframesInDom
                    .Where(ifr => !collectedFrameUrls.Contains(ifr.src) && !string.IsNullOrWhiteSpace(ifr.src))
                    .ToList();
                if (uncapturedIframes.Count > 0)
                {
                    diagLog.Add($"CrossOriginIframe: {uncapturedIframes.Count} 个跨域 iframe 已跳过采集（兜底已移除，元素无法 click）");
                }
            }

            // ★ 5. 增强诊断：采集页面级指标（Body HTML 长度、DOM 元素总数、交互元素数、弹窗数、ShadowRoot 数）
            //    任何网站出问题基本一眼定位是“元素被截断 / 弹窗未渲染 / frame 未创建 / shadow 未穿透”
            int bodyHtmlLength = 0, totalDomCount = 0, interactiveCount = 0, modalCount = 0, shadowRootCount = 0;
            try
            {
                var metricsJson = await _page!.EvaluateExpressionAsync<string>(@"
                    JSON.stringify((function(){
                        var body = document.body;
                        var bodyLen = body ? body.innerHTML.length : 0;
                        var totalDom = document.querySelectorAll('*').length;
                        var interactive = document.querySelectorAll('input,button,textarea,select,a,[role=""button""],div[onclick],span[onclick],[class*=""btn""],[class*=""confirm""],[class*=""submit""],[class*=""ok""],[class*=""cancel""],[class*=""close""]').length;
                        var modals = document.querySelectorAll('[role=""dialog""],.el-dialog,.el-dialog__wrapper,.v-modal,.layui-layer,.layui-layer-page,.modal,.modal-dialog,.ant-modal,.ant-modal-wrap,[class*=""modal""],[class*=""dialog""],[class*=""popup""],[class*=""layer""],[class*=""drawer""]').length;
                        var shadow = 0;
                        document.querySelectorAll('*').forEach(function(el){ if(el.shadowRoot) shadow++; });
                        return {bodyLen:bodyLen,totalDom:totalDom,interactive:interactive,modals:modals,shadow:shadow};
                    })())
                ");
                if (!string.IsNullOrWhiteSpace(metricsJson) && metricsJson != "null")
                {
                    var metrics = JsonSerializer.Deserialize<Dictionary<string, object>>(metricsJson);
                    if (metrics != null)
                    {
                        bodyHtmlLength = GetInt(metrics, "bodyLen");
                        totalDomCount = GetInt(metrics, "totalDom");
                        interactiveCount = GetInt(metrics, "interactive");
                        modalCount = GetInt(metrics, "modals");
                        shadowRootCount = GetInt(metrics, "shadow");
                    }
                }
            }
            catch (Exception) { }

            // ★ 6. 诊断日志写入文件（每次追加最新状态）
            try
            {
                var diagPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "debug-ba-frame-diag.log");
                // 确保路径存在
                var diagDir = Path.GetDirectoryName(diagPath);
                if (!string.IsNullOrWhiteSpace(diagDir) && !Directory.Exists(diagDir))
                    Directory.CreateDirectory(diagDir);

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var diagLines = new List<string>
                {
                    $"=== [{timestamp}] GetElementsAsync diag ===",
                    $"PageURL: {_page.Url ?? ""}",
                    $"FrameCount (excl main): {_page.Frames.Count(f => f != _page.MainFrame)}",
                    $"FrameURLs: {string.Join(" | ", _page.Frames.Where(f => f != _page.MainFrame).Select(f => f.Url ?? ""))}",
                    $"BodyHtmlLength: {bodyHtmlLength}",
                    $"TotalDomCount: {totalDomCount}",
                    $"InteractiveElementCount (page): {interactiveCount}",
                    $"ModalCount (page): {modalCount}",
                    $"ShadowRootCount: {shadowRootCount}",
                    $"IframesInDOM: {iframesInDom.Count}",
                    $"IframesInDOM details: {string.Join("; ", iframesInDom.Select(i => $"{i.id}={i.src}"))}",
                    $"CollectedFrameURLs: {string.Join("; ", collectedFrameUrls)}",
                    $"CollectedFromAnyFrame: {collectedFromAnyFrame}",
                    $"FrameRetryCount: {frameRetryCount}",
                    $"TotalElements before sorting: {allElements.Count}",
                    $"InModal: {allElements.Count(e => GetBool(e, "in_modal"))}",
                    $"InFrame: {allElements.Count(e => GetBool(e, "in_frame"))}",
                    $"PerDocumentLimit: {PerDocumentElementLimit}",
                    $"MaxReturnedElements: {MaxReturnedElements}",
                };
                diagLines.AddRange(diagLog.Select(l => "  " + l));
                diagLines.Add($"=== end ===");

                File.AppendAllLines(diagPath, diagLines, System.Text.Encoding.UTF8);
            }
            catch (Exception) { }

            // 6. 全局排序：弹窗内元素 > iframe 内元素 > 普通元素
            var sortedElements = allElements
                .OrderByDescending(el => GetBool(el, "in_modal"))
                .ThenByDescending(el => GetBool(el, "in_frame"))
                .Take(MaxReturnedElements)
                .ToList();

            var combinedPageText = string.Join(" ", pageTextParts).Replace("  ", " ").Trim();
            if (combinedPageText.Length > 4000) combinedPageText = combinedPageText.Substring(0, 4000);

            return JsonSerializer.Serialize(new { elements = sortedElements, page_text = combinedPageText });
        }

        // 解析单 frame 的采集结果并合并到全局列表
        private void ParseElementsResult(string json, List<Dictionary<string, object>> allElements, List<string> pageTextParts, bool isFrame, string frameUrl, string frameName)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                if (data == null) return;

                if (data.TryGetValue("page_text", out var pageTextObj) && pageTextObj is JsonElement pageTextJson && pageTextJson.ValueKind == JsonValueKind.String)
                {
                    var framePageText = pageTextJson.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(framePageText))
                    {
                        // 主文档直接追加；iframe 文档加标记，便于 AI 识别弹窗/frame 上下文
                        if (isFrame)
                        {
                            pageTextParts.Add($"[FRAME {frameUrl}] {framePageText}");
                        }
                        else
                        {
                            pageTextParts.Add(framePageText);
                        }
                    }
                }

                if (data.TryGetValue("elements", out var elementsObj) && elementsObj is JsonElement elementsJson && elementsJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in elementsJson.EnumerateArray())
                    {
                        var elDict = el.Deserialize<Dictionary<string, object>>();
                        if (elDict == null) continue;
                        elDict["in_frame"] = isFrame;
                        elDict["frame_url"] = frameUrl;
                        elDict["frame_name"] = frameName;
                        // 只要是通过 frame API 采集的，统一标记为 in_iframe=true
                        if (isFrame) elDict["in_iframe"] = true;
                        allElements.Add(elDict);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ParseElementsResult failed: {ex.Message}");
            }
        }

        // 在浏览器执行任意 JS 表达式并返回 JSON 序列化结果
        public async Task<string> EvaluateAsync(string jsCode)
        {
            EnsureStarted();
            var result = await _page!.EvaluateExpressionAsync<object>(jsCode);
            if (result == null) return "null";
            // 直接 Serialize object 可能丢失原始结构，这里包装为 JsonElement 再序列化以保留运行时类型
            if (result is JsonElement je)
            {
                return je.ValueKind == JsonValueKind.Null ? "null" : je.GetRawText();
            }
            return JsonSerializer.Serialize(result);
        }

        // 单纯延迟，不做任何浏览器操作
        public async Task WaitAsync(int ms)
        {
            await Task.Delay(ms);
        }

        // 关闭浏览器并释放资源
        public async Task StopAsync()
        {
            if (_browser != null)
            {
                if (!_browser.IsClosed)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        var closeTask = _browser.CloseAsync();
                        await Task.WhenAny(closeTask, Task.Delay(-1, cts.Token));
                    }
                    catch (Exception) { }
                }
                try { _browser.Dispose(); } catch (Exception) { }
                _browser = null;
                _page = null;

                // 强制清理本次 BA 启动的 chromium/chrome 子进程（CloseAsync 卡住时兜底）
                await Task.Delay(500);
                KillLaunchedChromeProcesses();
            }
        }

        private void KillLaunchedChromeProcesses()
        {
            try
            {
                var currentChrome = Process.GetProcessesByName("chrome");
                foreach (var proc in currentChrome)
                {
                    if (_preLaunchChromePids.Contains(proc.Id)) continue;
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            proc.WaitForExit(3000);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }

        // 返回当前运行状态对象
        public async Task<object> GetStatusAsync()
        {
            bool running = _browser != null && !_browser.IsClosed;
            string url = string.Empty;
            string title = string.Empty;

            if (running && _page != null)
            {
                try
                {
                    url = _page.Url ?? string.Empty;
                    title = await _page.GetTitleAsync();
                }
                catch (Exception) { }
            }

            return new { running, url, title };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // 同步等待，但带超时避免死锁；若超时则强制清理进程
                var stopTask = StopAsync();
                if (!stopTask.Wait(TimeSpan.FromSeconds(15)))
                {
                    KillLaunchedChromeProcesses();
                }
            }
            catch (Exception) { }
        }

        // ── 内部辅助 ───────────────────────────────────────

        private void EnsureStarted()
        {
            if (_browser == null || _browser.IsClosed || _page == null)
            {
                throw new InvalidOperationException("浏览器未启动或已关闭，请先调用 StartAsync");
            }
        }

        // 从 Dictionary 中安全读取 bool 值（兼容 JsonElement true / C# bool）
        private static bool GetBool(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var obj)) return false;
            if (obj is bool b) return b;
            if (obj is JsonElement je)
            {
                return je.ValueKind == JsonValueKind.True;
            }
            return false;
        }

        // ★ 从 Dictionary 中安全读取 int 值（兼容 JsonElement number / C# int/long）
        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var obj)) return 0;
            if (obj is int i) return i;
            if (obj is long l) return (int)l;
            if (obj is double d) return (int)d;
            if (obj is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number)
                {
                    return je.TryGetInt32(out var iv) ? iv : (int)je.GetDouble();
                }
            }
            return 0;
        }

        // ★ 安全等待网络空闲：捕获超时异常，不阻断主流程
        //   原因：某些页面有长轮询（如宝塔面板实时监控），networkidle 可能永远不满足，
        //   超时后继续执行 DOM 稳定检测即可，不应阻断操作。
        //   Playwright 的 networkidle 策略：500ms 内无网络请求（Concurrency=0）视为空闲。
        private async Task WaitForNetworkIdleSafeAsync()
        {
            try
            {
                await _page!.WaitForNetworkIdleAsync(new WaitForNetworkIdleOptions
                {
                    Timeout = NetworkIdleTimeoutMs,
                    IdleTime = NetworkIdleTimeMs,
                });
            }
            catch (Exception)
            {
                // 超时或页面导航中，忽略，继续后续 DOM 稳定检测
            }
        }

        // ★ 等待 DOM 稳定：轮询 document.querySelectorAll('*').length，
        //   元素数量在 DomStableThresholdMs 内无变化视为渲染完成。
        //   适配 SPA 路由切换（pushState 不触发 navigation）、传统页面导航、弹窗 AJAX 渲染三种场景。
        //   导航过程中 DOM 可能短暂不可访问（EvaluateExpressionAsync 抛异常），捕获后继续轮询。
        private async Task WaitForDomStableAsync()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(DomStableMaxWaitMs);
            int lastCount = -1;
            var lastChange = DateTime.UtcNow;

            while (DateTime.UtcNow < deadline)
            {
                int currentCount;
                try
                {
                    currentCount = await _page!.EvaluateExpressionAsync<int>("document.querySelectorAll('*').length");
                }
                catch (Exception)
                {
                    // 页面导航中 DOM 短暂不可访问，重置基准继续等待
                    await Task.Delay(DomStablePollMs);
                    lastChange = DateTime.UtcNow;
                    continue;
                }

                if (currentCount == lastCount)
                {
                    if ((DateTime.UtcNow - lastChange).TotalMilliseconds >= DomStableThresholdMs)
                    {
                        return; // 元素数量稳定，视为渲染完成
                    }
                }
                else
                {
                    lastCount = currentCount;
                    lastChange = DateTime.UtcNow;
                }

                await Task.Delay(DomStablePollMs);
            }
            // 超过最大等待时长仍未稳定，直接返回（避免无限阻塞）
        }

        // ★ 基于 MutationObserver 的 DOM 稳定等待：监听 body 子树变化，300ms 内无 mutation 视为稳定
        //   比固定轮询 querySelectorAll('*').length 更精准：能捕获属性/文本变化（元素数量可能不变但内容在变）
        //   带兜底超时（5s），防止长轮询页面永久阻塞；立即触发一次，若 DOM 已稳定 300ms 后自动结束
        private async Task WaitForDomStableMutationAsync()
        {
            try
            {
                await _page!.EvaluateFunctionAsync(@"() => new Promise(resolve => {
                    let settled = false;
                    let observer;
                    const finish = () => {
                        if (settled) return;
                        settled = true;
                        try { if (observer) observer.disconnect(); } catch(e){}
                        clearTimeout(window.__baMutationTimer);
                        resolve();
                    };
                    try {
                        observer = new MutationObserver(() => {
                            clearTimeout(window.__baMutationTimer);
                            window.__baMutationTimer = setTimeout(finish, 300);
                        });
                        observer.observe(document.body, { childList: true, subtree: true });
                    } catch(e) {
                        // body 不存在或不可观察，立即返回
                        finish();
                        return;
                    }
                    // 兜底超时：最多等待 5 秒
                    setTimeout(finish, 5000);
                    // 立即触发一次：若 DOM 已稳定，300ms 后自动结束
                    clearTimeout(window.__baMutationTimer);
                    window.__baMutationTimer = setTimeout(finish, 300);
                })");
            }
            catch (Exception)
            {
                // 页面导航中或 body 不可访问时忽略，回退到轮询方式
            }
        }

        // ★ 检测页面是否存在弹窗/模态框/浮层；若存在，额外等待内容渲染完成
        //   适用场景：点击"添加站点"等按钮后，弹窗动画 + iframe/表单异步加载需要额外时间
        private async Task WaitForModalContentAsync()
        {
            try
            {
                var hasModal = await _page!.EvaluateExpressionAsync<bool>(@"
                    !!document.querySelector('[role=""dialog""], .el-dialog, .el-dialog__wrapper, .v-modal, .el-message-box, .el-drawer, .ant-modal, .ant-modal-root, .ant-modal-wrap, .layui-layer, .layui-layer-page, .layui-layer-content, .modal, .modal-dialog, .bt-form, .bt-popup, .el-popup-parent--hidden, [class*=""modal""], [class*=""dialog""], [class*=""popup""], [class*=""layer""], [class*=""drawer""]')
                ");
                if (hasModal)
                {
                    // ★ 弹窗容器已出现—主等待：让内部表单/iframe 完成渲染和动画
                    //   MutationObserver 精准等待 mutation 停止（5s 兜底）+ 轮询 DOM 稳定兜底
                    await WaitForDomStableMutationAsync();
                    await WaitForDomStableAsync();

                    // ★ 二次稳定检查：弹窗内 input/select/textarea < 2 时再等一次 MutationObserver
                    //   （原 sleep + DomStable 改为仅 MutationObserver，节省 ~3s）
                    try
                    {
                        var modalInputCount = await _page!.EvaluateExpressionAsync<int>(@"
                            (function() {
                                var container = document.querySelector('[role=""dialog""], .el-dialog, .el-dialog__wrapper, .v-modal, .layui-layer, .layui-layer-page, .modal, .modal-dialog, [class*=""modal""], [class*=""dialog""], [class*=""popup""], [class*=""layer""]');
                                if (!container) return 0;
                                return container.querySelectorAll('input, select, textarea, button').length;
                            })()
                        ");
                        if (modalInputCount < 2)
                        {
                            await WaitForDomStableMutationAsync();
                        }
                    }
                    catch (Exception)
                    {
                        // 二次检查失败不阻断
                    }

                    // ★ 三次检查：等待弹窗内 iframe 的内容就绪（宝塔弹窗用 iframe 加载表单）
                    //   对每个子 frame 等待可见交互元素出现，最多 2 秒（原 5 秒）
                    try
                    {
                        foreach (var frame in _page.Frames)
                        {
                            if (frame == _page.MainFrame) continue;
                            try
                            {
                                await frame.WaitForFunctionAsync(
                                    VisibleInteractiveCheckJs,
                                    new WaitForFunctionOptions { Timeout = 2000, PollingInterval = 200 }
                                );
                            }
                            catch (Exception)
                            {
                                // 超时不阻断：frame 内可能确实无交互元素
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // frame 遍历失败不阻断
                    }
                }
            }
            catch (Exception)
            {
                // 页面导航中或 DOM 不可访问时忽略，不阻断主流程
            }
        }

        // 统一的元素等待逻辑：支持标准 CSS 选择器 + Playwright 风格的 :has-text("xxx") 文本选择器
        // ★ 关键改进：遍历所有 frame 查找元素，支持跨域 iframe 内元素操作
        private async Task<IElementHandle> WaitForSelectorSafe(string selector)
        {
            try
            {
                // 1. 优先主 frame
                var mainElement = await TryWaitForSelectorInFrameAsync(_page!.MainFrame, selector);
                if (mainElement != null) return mainElement;

                // 2. 主 frame 未找到，依次尝试每个子 frame
                foreach (var frame in _page!.Frames)
                {
                    if (frame == _page.MainFrame) continue;
                    var frameElement = await TryWaitForSelectorInFrameAsync(frame, selector);
                    if (frameElement != null) return frameElement;
                }

                throw new InvalidOperationException($"元素 {selector} 未找到，页面可能未完全加载");
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) when (ex is TimeoutException || ex.GetType().Name.Contains("WaitTask"))
            {
                throw new InvalidOperationException($"元素 {selector} 未找到，页面可能未完全加载", ex);
            }
        }

        // 在单个 frame 中尝试查找元素；返回 null 表示未找到（不抛异常，便于跨 frame 重试）
        private async Task<IElementHandle?> TryWaitForSelectorInFrameAsync(IFrame frame, string selector)
        {
            try
            {
                // 检测 :has-text("xxx") 模式（Playwright 专用语法，PuppeteerSharp 不支持）
                var hasTextMatch = Regex.Match(
                    selector, @"^(?<tag>[a-zA-Z][\w-]*)?:has-text\(""(?<text>[^""]+)""\)$");

                if (hasTextMatch.Success)
                {
                    var tag = hasTextMatch.Groups["tag"].Value;
                    var text = hasTextMatch.Groups["text"].Value;
                    var tagSelector = string.IsNullOrEmpty(tag) ? "*" : tag;
                    var resolvedSelector = await frame.EvaluateFunctionAsync<string>(HasTextResolveJs, tagSelector, text);
                    if (string.IsNullOrEmpty(resolvedSelector))
                    {
                        return null;
                    }
                    return await frame.WaitForSelectorAsync(resolvedSelector, new WaitForSelectorOptions
                    {
                        Timeout = _config.ElementTimeoutMs,
                        Visible = true,
                    });
                }

                // 标准 CSS 选择器：走 PuppeteerSharp 原生 WaitForSelectorAsync
                return await frame.WaitForSelectorAsync(selector, new WaitForSelectorOptions
                {
                    Timeout = _config.ElementTimeoutMs,
                    Visible = true,
                });
            }
            catch
            {
                // 当前 frame 未找到或执行失败，返回 null 让上层尝试其他 frame
                return null;
            }
        }
    }
}
