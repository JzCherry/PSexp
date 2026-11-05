using Microsoft.Win32;

namespace PsExporter.Services;

internal static class WindowsStartup
{
    internal const string Argument = "--psexp-silent-startup";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "PSexpV3";

    internal static bool CanConfigure => OperatingSystem.IsWindows() && !AppProfile.IsTestBuild;

    internal static (bool Exists, string? ExecutablePath) ReadRegistration()
    {
        if (!CanConfigure) return default;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return (value is not null, GetRegisteredExecutable(value as string));
    }

    internal static string? GetRegisteredExecutable(string? command)
    {
        var suffix = $"\" {Argument}";
        if (command is null || command.Length <= suffix.Length + 1
            || !command.StartsWith('"') || !command.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var path = command[1..^suffix.Length];
        return Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }

    internal static string CommandFor(string path) => $"\"{path}\" {Argument}";

    internal static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!CanConfigure) throw new InvalidOperationException("测试版不登记开机启动");
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
