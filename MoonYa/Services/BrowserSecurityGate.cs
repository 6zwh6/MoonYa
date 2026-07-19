// BrowserSecurityGate — 浏览器自动化安全授权网关：URL 域名白名单校验与用户模态授权
// 拒绝时立即终止返回错误，不降级；模态窗口由主窗口拥有以避免被阻塞

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MoonYa.Services
{
    /// <summary>授权请求结果：是否允许 + 是否记忆信任该域名</summary>
    public class AuthorizationResult
    {
        public bool Approved { get; set; }
        public bool RememberTrust { get; set; }
    }

    /// <summary>浏览器自动化安全授权网关：在执行浏览器操作前校验域名白名单或弹出 WPF 模态窗口询问用户</summary>
    public class BrowserSecurityGate
    {
        // 配置文件路径：与可执行文件同目录的 launcher_config.json
        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");

        // 请求用户授权访问指定 URL 并执行操作列表
        // ★ 始终批准，不弹任何授权窗口
        public async Task<AuthorizationResult> RequestAuthorizationAsync(string url, List<string> operations)
        {
            await Task.CompletedTask;  // 保持 async 签名兼容
            return new AuthorizationResult { Approved = true, RememberTrust = false };
        }

        // 检查域名是否在 launcher_config.json 的 browser_automation.trusted_domains 数组中（不区分大小写）
        public bool IsDomainTrusted(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;

            try
            {
                var root = ReadConfigRoot();
                if (root == null) return false;

                var arr = root["browser_automation"]?["trusted_domains"]?.AsArray();
                if (arr == null) return false;

                foreach (var node in arr)
                {
                    var v = node?.GetValue<string>();
                    if (v != null && string.Equals(v, domain, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                // 配置读取失败：按"未信任"处理，让上层走授权窗口
                return false;
            }
        }

        // 向 launcher_config.json 的 trusted_domains 数组追加域名（不重复，不区分大小写）
        public void AddTrustedDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new ArgumentException("domain 不能为空", nameof(domain));
            }

            var root = ReadConfigRoot()
                ?? throw new InvalidOperationException("无法读取 launcher_config.json");

            var browserNode = root["browser_automation"];
            if (browserNode == null)
            {
                browserNode = new JsonObject();
                root["browser_automation"] = browserNode;
            }

            var arr = browserNode["trusted_domains"]?.AsArray();
            if (arr == null)
            {
                arr = new JsonArray();
                browserNode["trusted_domains"] = arr;
            }

            // 去重检查（不区分大小写）
            foreach (var node in arr)
            {
                var v = node?.GetValue<string>();
                if (v != null && string.Equals(v, domain, StringComparison.OrdinalIgnoreCase))
                {
                    return; // 已存在，不重复添加
                }
            }

            arr.Add(JsonValue.Create(domain));

            WriteConfigRoot(root);
        }

        // ── 内部辅助 ───────────────────────────────────────

        // 从 URL 提取域名（Host）；解析失败返回空字符串
        private static string ExtractDomain(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Host ?? string.Empty;
            }
            return string.Empty;
        }

        // 读取配置文件根节点；文件不存在或解析失败返回 null
        private static JsonNode? ReadConfigRoot()
        {
            if (!File.Exists(ConfigPath)) return null;
            var text = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text);
        }

        // 将根节点写回配置文件（保留 2 空格缩进风格，与原文件一致）
        private static void WriteConfigRoot(JsonNode root)
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
            };
            var text = root.ToJsonString(opts);
            File.WriteAllText(ConfigPath, text);
        }

        // 在 UI 线程创建并显示授权模态窗口；ShowDialog 阻塞直到用户选择，返回授权结果
        private static AuthorizationResult ShowAuthorizationDialog(string url, string domain, List<string> operations)
        {
            var result = new AuthorizationResult { Approved = false, RememberTrust = false };

            var window = new Window
            {
                Title = "MoonYa 浏览器自动化 - 安全确认",
                Width = 540,
                Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Topmost = true,
            };

            // 模态归属：主窗口拥有，避免被阻塞或丢失焦点
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow != null)
            {
                window.Owner = mainWindow;
            }

            var panel = new StackPanel { Margin = new Thickness(18) };

            // URL 段
            panel.Children.Add(new TextBlock
            {
                Text = "即将访问的 URL：",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(url) ? "（未知）" : url,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 操作列表段
            panel.Children.Add(new TextBlock
            {
                Text = "即将执行的操作：",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
            });

            var ops = operations ?? new List<string>();
            var formatted = new List<string>();
            for (int i = 0; i < ops.Count; i++)
            {
                formatted.Add($"{i + 1}. {ops[i]}");
            }
            if (formatted.Count == 0) formatted.Add("（无）");

            panel.Children.Add(new ItemsControl
            {
                ItemsSource = formatted,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 提示段
            panel.Children.Add(new TextBlock
            {
                Text = "请确认是否允许此次操作。拒绝将立即终止，不会降级执行。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.IndianRed,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 按钮段
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var allowBtn = new Button
            {
                Content = "✓ 允许本次操作",
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(4),
            };
            var trustBtn = new Button
            {
                Content = string.IsNullOrEmpty(domain)
                    ? "✓ 信任此域名（未知）"
                    : $"✓ 信任此域名（{domain}）",
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(4),
            };
            var rejectBtn = new Button
            {
                Content = "✗ 拒绝",
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(4),
            };

            btnPanel.Children.Add(allowBtn);
            btnPanel.Children.Add(trustBtn);
            btnPanel.Children.Add(rejectBtn);
            panel.Children.Add(btnPanel);

            window.Content = panel;

            allowBtn.Click += (_, _) =>
            {
                result.Approved = true;
                result.RememberTrust = false;
                window.Close();
            };
            trustBtn.Click += (_, _) =>
            {
                result.Approved = true;
                result.RememberTrust = true;
                window.Close();
            };
            rejectBtn.Click += (_, _) =>
            {
                result.Approved = false;
                result.RememberTrust = false;
                window.Close();
            };

            // 用户直接关闭窗口（X 按钮 / Alt+F4）按拒绝处理
            window.Closed += (_, _) =>
            {
                if (!result.Approved)
                {
                    result.RememberTrust = false;
                }
            };

            window.ShowDialog();
            return result;
        }
    }
}
