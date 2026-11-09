using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PsExporter.Core.Process;

/// <summary>
/// 跨平台 Photoshop 进程探测
///  - Windows: GetProcessesByName + 注册表
///  - macOS:   pgrep / osascript
///  - Linux:   不会运行 PS，始终返回 NotRunning
/// </summary>
public sealed class PhotoshopDetector : IPhotoshopDetector
{
    private const string WinProcName = "Photoshop";
    private static (int ProcessId, string ExecutablePath, string Version) _cachedWinProcess;

    public PhotoshopProcessInfo Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DetectWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return DetectMac();
        return PhotoshopProcessInfo.NotRunning;
    }

    public async Task<bool> TryLaunchAsync(string? explicitPath, CancellationToken ct = default)
    {
        try
        {
            var path = explicitPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                var info = Detect();
                path = info.ExecutablePath;
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            // 等待进程出现
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(1000, ct);
                if (Detect().IsRunning) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static PhotoshopProcessInfo DetectWindows()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(WinProcName);
        if (procs.Length == 0)
            return PhotoshopProcessInfo.NotRunning;

        try
        {
            var process = procs[0];
            var cached = _cachedWinProcess;
            if (cached.ProcessId == process.Id && !string.IsNullOrEmpty(cached.ExecutablePath))
            {
                return new PhotoshopProcessInfo
                {
                    IsRunning = true,
                    ProcessId = process.Id,
                    ExecutablePath = cached.ExecutablePath,
                    Version = cached.Version
                };
            }
            var path = TryGetWindowsPath(process);
            var version = TryGetProcessVersion(process);
            if (string.IsNullOrWhiteSpace(version))
                version = TryGetWindowsVersion();
            _cachedWinProcess = (process.Id, path, version);

            return new PhotoshopProcessInfo
            {
                IsRunning = true,
                ProcessId = process.Id,
                ExecutablePath = path,
                Version = version
            };
        }
        finally
        {
            foreach (var process in procs)
                process.Dispose();
        }
    }

    private static string TryGetWindowsPath(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // 访问被拒时走注册表。
        }

        try
        {
            // HKLM\SOFTWARE\Adobe\Photoshop\<ver>\ApplicationPath
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Adobe\Photoshop");
            if (key != null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var appKey = key.OpenSubKey(sub + @"\ApplicationPath");
                    var v = appKey?.GetValue("ApplicationPath") as string;
                    if (!string.IsNullOrEmpty(v))
                    {
                        var exe = Path.Combine(v, "Photoshop.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static string TryGetProcessVersion(System.Diagnostics.Process process)
    {
        try
        {
            var version = process.MainModule?.FileVersionInfo;
            if (version is null || version.ProductMajorPart <= 0)
                return string.Empty;

            return version.ProductMinorPart > 0
                ? $"{version.ProductMajorPart}.{version.ProductMinorPart}"
                : version.ProductMajorPart.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetWindowsVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Adobe\Photoshop");
            if (key != null)
            {
                return key.GetSubKeyNames()
                    .Select(name => (Name: name, Parsed: Version.TryParse(name, out var version) ? version : null))
                    .Where(item => item.Parsed is not null)
                    .OrderByDescending(item => item.Parsed)
                    .Select(item => item.Name)
                    .FirstOrDefault() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private static PhotoshopProcessInfo DetectMac()
    {
        try
        {
            var psi = new ProcessStartInfo("pgrep", "-ix \"Adobe Photoshop\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return PhotoshopProcessInfo.NotRunning;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            if (string.IsNullOrWhiteSpace(stdout))
                return PhotoshopProcessInfo.NotRunning;

            var pidStr = stdout.Trim().Split('\n')[0].Trim();
            int.TryParse(pidStr, out var pid);

            var path = TryGetMacPath();
            var ver = TryGetMacVersion();
            return new PhotoshopProcessInfo
            {
                IsRunning = true,
                ProcessId = pid,
                ExecutablePath = path,
                Version = ver
            };
        }
        catch
        {
            return PhotoshopProcessInfo.NotRunning;
        }
    }

    private static string TryGetMacPath()
    {
        try
        {
            // osascript -e 'POSIX path of (path to application id "com.adobe.Photoshop")'
            var script = "POSIX path of (path to application id \"com.adobe.Photoshop\")";
            var psi = new ProcessStartInfo("osascript", "-e " + script.Quote())
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return string.Empty;
            var s = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return s;
        }
        catch { return string.Empty; }
    }

    private static string TryGetMacVersion()
    {
        try
        {
            var script = "version of application id \"com.adobe.Photoshop\"";
            var psi = new ProcessStartInfo("osascript", "-e " + script.Quote())
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return string.Empty;
            var s = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return s;
        }
        catch { return string.Empty; }
    }
}

internal static class MacShellExtensions
{
    public static string Quote(this string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
