using Microsoft.Win32;

namespace PsExporter.Services;

internal static class WindowsStartup
{
    internal const string Argument = "--psexp-silent-startup";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "PSexpV3";

    internal static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    internal static string CommandFor(string path) => $"\"{path}\" {Argument}";

    internal static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var path = Environment.ProcessPath;
        if (path is null || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请从 PSexp EXE 启用开机启动");
        key.SetValue(ValueName, CommandFor(path), RegistryValueKind.String);
    }
}
