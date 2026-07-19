using System;
using System.Diagnostics;
using System.IO;

namespace MoonYa.Services
{
    public class SearchServerService
    {
        private Process? _process;
        private readonly int _port;
        private readonly string _scriptPath;

        public int Port => _port;
        public bool IsRunning => _process != null && !_process.HasExited;

        public SearchServerService(int port = 58902)
        {
            _port = port;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _scriptPath = FindScript(baseDir);
        }

        private static string FindScript(string baseDir)
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "MoonYa-Python", "web_search", "search_server.py")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "MoonYa-Python", "web_search", "search_server.py")),
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

            if (!File.Exists(_scriptPath))
            {
                System.Diagnostics.Debug.WriteLine($"SearchServerService: Script not found at {_scriptPath}");
                return;
            }

            var pythonExe = FindPython();
            if (pythonExe == null)
            {
                System.Diagnostics.Debug.WriteLine("SearchServerService: python.exe not found");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{_scriptPath}\" 127.0.0.1 {_port}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _process = new Process { StartInfo = startInfo };
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    System.Diagnostics.Debug.WriteLine($"[search-server] {e.Data}");
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    System.Diagnostics.Debug.WriteLine($"[search-server-err] {e.Data}");
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            System.Diagnostics.Debug.WriteLine($"SearchServerService: Started on port {_port}");
        }

        public void Stop()
        {
            if (_process == null) return;

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

            System.Diagnostics.Debug.WriteLine("SearchServerService: Stopped.");
        }

        private static string? FindPython()
        {
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
