namespace PsExporter;

internal static class AppProfile
{
    internal static bool IsTestBuild { get; } =
#if PSEXP_TEST_BUILD
        true;
#else
        false;
#endif

    internal static string InstanceId => IsTestBuild ? "PSexpV3.Test" : "PSexpV3";

    internal static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        IsTestBuild ? "PsExporter-Test" : "PsExporter");

    internal static string LocalDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        IsTestBuild ? "PSexp-Test" : "PSexp");
}
