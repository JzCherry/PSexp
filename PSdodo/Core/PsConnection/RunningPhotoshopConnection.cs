using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace PsExporter.Core.PsConnection;

/// <summary>Attach to a live Photoshop object in a compatible permission context; never activate a server.</summary>
internal static class RunningPhotoshopConnection
{
    private sealed record Registration(string ProgId, Guid ClassId, string ServerPath);
    private static readonly Lazy<Registration[]> Registrations = new(ReadRegistrations);
    // Photoshop's _Application automation interface (also registered by the installed Photoshop type library).
    private static readonly Guid ApplicationInterfaceId = new("5DE90358-4D0B-4FA1-BA3E-C91BBA863F32");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> ProcessPathCache = new();
    private static (Guid ClassId, string ServerPath) _verifiedRegistration;
    private static readonly int CurrentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

    public static object Attach(out string description)
    {
        var runningPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runningIds = new HashSet<int>();
        var sessionId = CurrentSessionId;


        var processes = System.Diagnostics.Process.GetProcessesByName("Photoshop");
        try
        {
            if (processes.Length == 0) throw new PsNotConnectedException("Photoshop 未运行");
            foreach (var process in processes)
                try
                {
                    if (process.SessionId != sessionId) continue;
                    runningIds.Add(process.Id);
                    if (!ProcessPathCache.TryGetValue(process.Id, out var path))
                    {
                        path = process.MainModule?.FileName ?? "";
                        if (!string.IsNullOrEmpty(path)) ProcessPathCache[process.Id] = path;
                    }
                    if (!string.IsNullOrEmpty(path)) runningPaths.Add(path);
                }
                catch { /* Access restrictions must not turn process detection into 'no document'. */ }
        }
        finally { foreach (var process in processes) process.Dispose(); }

        // Diagnose integrity isolation before ROT lookup instead of waiting for a COM activation timeout.
        WindowsProcessPermissions.EnsureCompatible(runningIds);
        var registrations = Registrations.Value;
        var matching = registrations.Where(item => runningPaths.Contains(item.ServerPath)).ToArray();
        // Never attach to a different installed version when the running EXE has a matching registration.
        var candidates = (matching.Length > 0 ? matching : registrations)
            .OrderByDescending(item => item.ProgId == "Photoshop.Application");
        var tried = new HashSet<Guid>();
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!tried.Add(candidate.ClassId)) continue;
            var classId = candidate.ClassId;
            var result = GetActiveObject(ref classId, IntPtr.Zero, out var application);
            if (result >= 0 && application is not null && ((candidate.ClassId, candidate.ServerPath) == _verifiedRegistration && runningPaths.Contains(candidate.ServerPath) || MatchesRunningInstallation(application, runningPaths)))
            {
                _verifiedRegistration = (candidate.ClassId, candidate.ServerPath);
                description = $"连接已运行实例：{candidate.ProgId}；CLSID={classId}；server={candidate.ServerPath}";
                return application;
            }
            if (application is not null && Marshal.IsComObject(application)) Marshal.ReleaseComObject(application);
            failures.Add(result >= 0
                ? $"{candidate.ProgId}/运行安装路径未匹配"
                : $"{candidate.ProgId}/0x{result:X8}");
        }
        // Some recent Photoshop versions expose a live application under a ROT identity that differs
        // from the registered ProgID/CLSID. Discover by interface, then validate the running EXE path.
        if (TryAttachFromRunningObjectTable(runningPaths, failures, out var discovered, out description))
            return discovered!;
        throw new PsNotConnectedException("Photoshop 正在运行，但无法连接其自动化接口。"
            + "已检查进程权限、注册名称及已运行对象表；未启动 Photoshop、未派发外部脚本。连接记录："
            + (failures.Count > 0 ? string.Join("；", failures) : "未找到可用的 COM 注册"));
    }

    private static bool TryAttachFromRunningObjectTable(HashSet<string> runningPaths, List<string> failures,
        out object? application, out string description)
    {
        application = null;
        description = string.Empty;
        if (runningPaths.Count == 0)
        {
            failures.Add("ROT: 无法核对运行进程的安装路径");
            return false;
        }
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        var checkedCount = 0;
        try
        {
            Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out table));
            table.EnumRunning(out enumerator);
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                object? candidate = null;
                try
                {
                    // GetObject only retrieves registered live objects. Never BindToObject or activate a class.
                    if (table.GetObject(moniker, out candidate) < 0 || candidate is null) continue;
                    checkedCount++;
                    if (!ExposesPhotoshopApplication(candidate) || !MatchesRunningInstallation(candidate, runningPaths)) continue;
                    application = candidate;
                    candidate = null; // Ownership passes to WindowsComBridge, which releases it on the same STA.
                    description = $"通过 ROT 接口发现 Photoshop 已运行实例；接口={ApplicationInterfaceId}；已核对运行安装路径";
                    return true;
                }
                catch (COMException) { /* A live ROT entry can disappear while being enumerated. */ }
                finally
                {
                    ReleaseCom(candidate);
                    ReleaseCom(moniker);
                }
            }
            failures.Add($"ROT: 已核对 {checkedCount} 个已运行对象，未发现可用 Photoshop 接口及路径匹配");
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            failures.Add($"ROT: 0x{error.HResult:X8}");
        }
        finally
        {
            ReleaseCom(enumerator);
            ReleaseCom(table);
        }
        return false;
    }

    private static bool ExposesPhotoshopApplication(object candidate)
    {
        IntPtr unknown = IntPtr.Zero, application = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(candidate);
            var interfaceId = ApplicationInterfaceId;
            return Marshal.QueryInterface(unknown, ref interfaceId, out application) >= 0;
        }
        finally
        {
            if (application != IntPtr.Zero) Marshal.Release(application);
            if (unknown != IntPtr.Zero) Marshal.Release(unknown);
        }
    }

    private static bool MatchesRunningInstallation(object candidate, HashSet<string> runningPaths)
    {
        try
        {
            dynamic photoshop = candidate;
            string directory = Convert.ToString(photoshop.Path, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return !string.IsNullOrWhiteSpace(directory)
                && runningPaths.Contains(Path.GetFullPath(Path.Combine(directory, "Photoshop.exe")));
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            try { Marshal.ReleaseComObject(value); } catch (InvalidComObjectException) { }
    }

    private static Registration[] ReadRegistrations()
    {
        var items = new List<Registration>();
        var names = new List<string> { "Photoshop.Application" };
        try
        {
            names.AddRange(Registry.ClassesRoot.GetSubKeyNames().Where(name => name.StartsWith("Photoshop.Application.", StringComparison.OrdinalIgnoreCase)
                && name["Photoshop.Application.".Length..].Split('.').All(part => part.Length > 0 && part.All(char.IsDigit))));
        }
        catch { /* The generic registration is still checked when enumeration is restricted. */ }
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(name + @"\CLSID");
                if (!Guid.TryParse(key?.GetValue(null)?.ToString(), out var classId)) continue;
                using var server = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + classId.ToString("B") + @"\LocalServer32");
                var command = server?.GetValue(null)?.ToString() ?? "";
                var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                var path = end >= 0 ? command[..(end + 4)].Trim().Trim('"') : "";
                items.Add(new Registration(name, classId, path));
            }
            catch { /* Keep checking the remaining installed versions. */ }
        }
        return items.ToArray();
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable table);

    [DllImport("oleaut32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid classId, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? application);
}
