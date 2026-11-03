using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using PsExporter.Core.Logging;

namespace PsExporter.Core.PsConnection;

/// <summary>Keep Photoshop automation in the same user/session/integrity context.</summary>
internal static class WindowsProcessPermissions
{
    internal const string RelaunchArgument = "--psexp-normal-permissions";
    internal sealed record AccessInfo(string UserSid, int Integrity)
    {
        public string Label => Integrity >= 0x3000 ? "管理员权限" : Integrity >= 0x2000 ? "普通权限" : "受限权限";
    }

    internal static AccessInfo? Read(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) return null;
            return ReadToken(token);
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    private static AccessInfo? ReadToken(IntPtr token)
    {
        // SID lengths are variable; ask Windows for the required buffer size.
        string? ReadSid(int informationClass)
        {
            GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var size);
            if (size <= 0) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                return GetTokenInformation(token, informationClass, buffer, size, out _)
                    ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        var user = ReadSid(1); // TokenUser
        var integritySid = ReadSid(25); // TokenIntegrityLevel
        return user is not null && integritySid is not null
            && int.TryParse(integritySid[(integritySid.LastIndexOf('-') + 1)..], out var integrity)
            ? new AccessInfo(user, integrity) : null;
    }

    internal static void EnsureCompatible(IEnumerable<int> photoshopProcessIds)
    {
        var current = Read(Environment.ProcessId);
        if (current is null) return;
        var targets = photoshopProcessIds.Select(Read).OfType<AccessInfo>().ToArray();
        if (targets.Length == 0 || targets.Any(target => target == current)) return;
        var sameUser = targets.FirstOrDefault(target => target.UserSid == current.UserSid);
        if (sameUser is not null && sameUser.Integrity != current.Integrity)
            throw new PsNotConnectedException($"Photoshop 与 PSexp 权限不一致（PSexp：{current.Label}；Photoshop：{sameUser.Label}）。"
                + (current.Integrity > sameUser.Integrity
                    ? "请完全退出 PSexp 后以普通权限打开，编译脚本也无需以管理员身份运行。"
                    : "请以相同权限运行两个程序；建议退出 Photoshop 后用普通权限重新打开。"))
            {
                UserHint = current.Integrity > sameUser.Integrity
                    ? "权限不一致，请退出后以普通权限打开 PSexp"
                    : "权限不一致，请以普通权限重新打开 Photoshop"
            };
    }

    /// <summary>Only lower our own privileges when Photoshop is running normally under this same user.</summary>
    internal static bool TryRelaunchForPhotoshop(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var logger = new FileLogger();
        var current = Read(Environment.ProcessId);
        if (current is null) return false;
        logger.Info($"[permissions] pid={Environment.ProcessId} PSexp={current.Label} integrity={current.Integrity}");
        if (current.Integrity < 0x3000) return false;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processes = System.Diagnostics.Process.GetProcessesByName("Photoshop");
        var needsRelaunch = false;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != self.SessionId) continue;
                    var target = Read(process.Id);
                    if (target?.UserSid != current.UserSid) continue;
                    logger.Info($"[permissions] Photoshop pid={process.Id} access={target.Label} integrity={target.Integrity}");
                    // If Photoshop itself is elevated, retain the matching context.
                    if (target.Integrity >= 0x3000) return false;
                    if (target.Integrity >= 0x2000) needsRelaunch = true;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    logger.Debug("[permissions] Photoshop 进程已退出或无法读取权限：" + error.Message);
                }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        if (!needsRelaunch) return false;
        if (args.Contains(RelaunchArgument, StringComparer.Ordinal))
        {
            logger.Warn("[permissions] 普通权限重启后仍不匹配，停止重试；请检查 EXE 的管理员兼容性设置。");
            return false;
        }

        IntPtr token = IntPtr.Zero, primary = IntPtr.Zero, environment = IntPtr.Zero;
        var stage = "desktop-token";
        try
        {
            // The linked token can be identification-only (error 1346). Use the same user's
            // actual normal desktop primary token instead, after checking user, session and integrity.
            token = OpenNormalDesktopToken(current, self.SessionId);
            stage = "duplicate-desktop-token";
            if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            stage = "create-user-environment";
            if (!CreateEnvironmentBlock(out environment, primary, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前 EXE 路径");
            // Preserve the original native command line, including a possible dotnet host and quoted arguments.
            var commandLine = new StringBuilder(Marshal.PtrToStringUni(GetCommandLineW()) + " " + RelaunchArgument);
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            stage = "start-normal-process";
            if (!CreateProcessWithTokenW(primary, 0, executable, commandLine, 0x400, environment,
                    Environment.CurrentDirectory, ref startup, out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { logger.Info($"[permissions] Photoshop 为普通权限；已使用当前账号的普通权限重新启动 PSexp pid={child.ProcessId}。"); }
            finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
            return true;
        }
        catch (Exception error)
        {
            logger.Warn($"[permissions] strategy=desktop-token stage={stage} 无法自动切回普通权限：" + error);
            return false;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static IntPtr OpenNormalDesktopToken(AccessInfo current, int sessionId)
    {
        var desktopWindow = GetShellWindow();
        if (desktopWindow == IntPtr.Zero) throw new InvalidOperationException("未找到桌面进程，请直接以普通权限打开 PSexp。");
        GetWindowThreadProcessId(desktopWindow, out var processId);
        using var desktop = System.Diagnostics.Process.GetProcessById(checked((int)processId));
        var access = Read(desktop.Id);
        if (desktop.SessionId != sessionId || access?.UserSid != current.UserSid || access.Integrity != 0x2000)
            throw new InvalidOperationException("桌面不是当前账号同会话的普通权限进程，未切换账号或启动程序。");
        var process = OpenProcess(0x1000, false, desktop.Id);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenProcessToken(process, 0xA, out var token)) // TOKEN_QUERY | TOKEN_DUPLICATE
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return token;
        }
        finally { CloseHandle(process); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCommandLineW();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int size, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int type, out IntPtr primary);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application, StringBuilder commandLine,
        uint creationFlags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
}
