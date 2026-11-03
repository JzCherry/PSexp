using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PsExporter.Core.Logging;

namespace PsExporter.Services;

// Immutable metadata can only be created inside the verified-manifest pipeline.
public sealed class UpdateManifest
{
    public string Name { get; }
    public string Version { get; }
    public string DownloadUrl { get; }
    public string Sha256 { get; }
    public long Size { get; }
    public string Changelog { get; }
    public bool RequiresDotNet { get; }

    internal UpdateManifest(UpdateManifestData data)
    {
        Name = data.Name ?? "";
        Version = data.Version ?? "";
        DownloadUrl = data.DownloadUrl ?? "";
        Sha256 = data.Sha256 ?? "";
        Size = data.Size;
        Changelog = data.Changelog ?? "";
        RequiresDotNet = data.RequiresDotNet;
    }
}

internal sealed class UpdateManifestData
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("changelog")] public string? Changelog { get; init; }
    [JsonPropertyName("requires_dotnet")] public bool RequiresDotNet { get; init; } = true;
}

/// <summary>固定公钥验证更新清单、限量下载、哈希校验与退出后的安全替换。</summary>
public sealed class UpdateService(FileLogger logger)
{
    public const string ManifestUrl = "https://yusnip.top/psexp/version.json";
    private const long MaximumDownload = 512L * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(10) };
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static bool IsNewer(string version) => Version.TryParse(version, out var value)
        && value > Version.Parse(CurrentVersion);
    public static bool IsSingleFileInstallation => !File.Exists(Path.Combine(AppContext.BaseDirectory, "PSexpV3.dll"))
        && Environment.ProcessPath is { } path && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public async Task<UpdateManifest> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (limited.Length + count > 128 * 1024) throw new IOException("更新清单过大");
            limited.Write(buffer, 0, count);
        }
        var info = UpdateSignature.ReadVerified(limited.ToArray());
        if (info.Name != "PSexp" || !Version.TryParse(info.Version, out var parsed) || parsed.Build < 0)
            throw new IOException("更新清单的软件或版本不正确");
        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "yusnip.top" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.AbsolutePath.StartsWith("/psexp/", StringComparison.Ordinal))
            throw new IOException("更新下载地址不受信任");
        if (info.Size <= 0 || info.Size > MaximumDownload || info.Sha256.Length != 64
            || !info.Sha256.All(Uri.IsHexDigit)) throw new IOException("更新清单缺少有效的文件大小或 SHA256");
        logger.Info($"更新检测 current={CurrentVersion} remote={info.Version} sha256={info.Sha256}");
        return info;
    }

    public async Task<string> DownloadAsync(UpdateManifest info, IProgress<double> progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        token = timeout.Token;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PsExporter", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PSexp.exe");
        try
        {
            using var response = await Client.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != info.Size)
                throw new IOException("更新文件大小与版本清单不一致");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                var clock = Stopwatch.StartNew();
                long lastReport = -200;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += read;
                    if (received > info.Size) throw new IOException("下载内容超出清单大小");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (clock.ElapsedMilliseconds - lastReport >= 120)
                    { progress.Report(received * 100d / info.Size); lastReport = clock.ElapsedMilliseconds; }
                }
                if (received != info.Size) throw new IOException("更新下载不完整");
                await output.FlushAsync(token);
            }
            await using var file = File.OpenRead(path);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!digest.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新文件 SHA256 校验失败");
            var downloadedVersion = FileVersionInfo.GetVersionInfo(path);
            var expected = Version.Parse(info.Version);
            if (downloadedVersion.FileMajorPart != expected.Major || downloadedVersion.FileMinorPart != expected.Minor
                || downloadedVersion.FileBuildPart != expected.Build) throw new IOException("更新文件内置版本与清单不一致");
            progress.Report(100);
            logger.Info($"更新包校验成功 version={info.Version} path={path}");
            return path;
        }
        catch { try { File.Delete(path); } catch { } throw; }
    }

    public void BeginInstall(UpdateManifest info, string downloadedPath)
    {
        if (!IsSingleFileInstallation) throw new IOException("目录开发版请使用编译脚本更新；自动更新仅适用于发布版单文件 EXE");
        if (info.RequiresDotNet && !HasSharedRuntime()) throw new IOException("新版需要 .NET 10 x64 Runtime，请先安装运行时再更新");
        var target = Path.GetFullPath(Environment.ProcessPath!);
        var destination = Regex.IsMatch(Path.GetFileName(target), @"^PSexp v\d+\.\d+(\.\d+)?\.exe$", RegexOptions.IgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(target)!, $"PSexp v{Version.Parse(info.Version).ToString(3)}.exe")
            : target;
        var script = Path.Combine(Path.GetDirectoryName(downloadedPath)!, "install.ps1");
        var dataPath = Path.Combine(Path.GetDirectoryName(downloadedPath)!, "install.json");
        File.WriteAllText(dataPath, JsonSerializer.Serialize(new
        {
            Target = target, Destination = destination, Source = downloadedPath,
            Sha256 = info.Sha256, Pid = Environment.ProcessId, Log = logger.LogPath,
            StartupKey = @"HKCU:\" + WindowsStartup.RunKey, StartupName = WindowsStartup.ValueName,
            OldStartupCommand = WindowsStartup.CommandFor(target), NewStartupCommand = WindowsStartup.CommandFor(destination)
        }), new UTF8Encoding(false));
        File.WriteAllText(script, InstallerScript, new UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(target)! };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, "-DataFile", dataPath })
            start.ArgumentList.Add(arg);
        if (Process.Start(start) is null) throw new IOException("无法启动更新安装程序");
        logger.Info($"准备退出并安装更新 version={info.Version} target={target}");
    }

    private static bool HasSharedRuntime()
    {
        var roots = new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"), Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root =>
        {
            var shared = Path.Combine(root!, "shared", "Microsoft.NETCore.App");
            return Directory.Exists(shared) && Directory.EnumerateDirectories(shared)
                .Any(path => Version.TryParse(Path.GetFileName(path), out var version) && version.Major == 10);
        });
    }

    private const string InstallerScript = """
param([Parameter(Mandatory=$true)][string]$DataFile)
$ErrorActionPreference='Stop'
$data=Get-Content -Raw -Encoding UTF8 -LiteralPath $DataFile | ConvertFrom-Json
$next=$data.Target+'.'+[guid]::NewGuid().ToString('N')+'.new'
$backup=$data.Target+'.'+[guid]::NewGuid().ToString('N')+'.bak'
$replaced=$false
$launch=$data.Target
$createdDestination=$false
$renamed=$false
$startupChanged=$false
function Write-UpdateLog([string]$message) {
    try { Add-Content -LiteralPath $data.Log -Value ('['+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')+'] [UpdateInstaller] '+$message) -Encoding UTF8 } catch { }
}
try {
    $old=Get-Process -Id $data.Pid -ErrorAction SilentlyContinue
    if($null -ne $old -and -not $old.WaitForExit(45000)){throw 'The app did not exit; update cancelled.'}
    if((Get-FileHash -LiteralPath $data.Source -Algorithm SHA256).Hash -ne $data.Sha256){throw 'Update checksum mismatch.'}
    Copy-Item -LiteralPath $data.Source -Destination $next
    if((Get-FileHash -LiteralPath $next -Algorithm SHA256).Hash -ne $data.Sha256){throw 'Staged checksum mismatch.'}
    for($attempt=0;$attempt -lt 30;$attempt++) {
        try { [IO.File]::Replace($next,$data.Target,$backup);$replaced=$true;break }
        catch { if($attempt -eq 29){throw};Start-Sleep -Milliseconds 500 }
    }
    if($data.Destination -ne $data.Target) {
        if(-not (Test-Path -LiteralPath $data.Destination)) {
            [IO.File]::Move($data.Target,$data.Destination)
            $createdDestination=$true
            $renamed=$true
        } elseif ((Test-Path -LiteralPath $data.Destination -PathType Leaf) -and
            (Get-FileHash -LiteralPath $data.Destination -Algorithm SHA256).Hash -eq $data.Sha256) {
            [IO.File]::Move($data.Target,$next)
            $renamed=$true
        } else {
            Write-UpdateLog ('Versioned filename already in use. Keeping '+$data.Target)
        }
        if($renamed) {
            $launch=$data.Destination
            $startup=Get-ItemProperty -LiteralPath $data.StartupKey -Name $data.StartupName -ErrorAction SilentlyContinue
            if($null -ne $startup -and $startup.($data.StartupName) -eq $data.OldStartupCommand) {
                Set-ItemProperty -LiteralPath $data.StartupKey -Name $data.StartupName -Value $data.NewStartupCommand
                $startupChanged=$true
            }
        }
    }
    Start-Process -FilePath $launch -WorkingDirectory (Split-Path -Parent $launch) -WindowStyle Normal
    Write-UpdateLog ('Update installed. Executable: '+$launch+'; rollback copy: '+$backup)
} catch {
    Write-UpdateLog ('Update failed: '+$_)
    if($replaced -and (Test-Path -LiteralPath $backup)) {
        try {
            if($renamed) { [IO.File]::Move($backup,$data.Target) }
            else { [IO.File]::Replace($backup,$data.Target,$null) }
            if($createdDestination) { Remove-Item -LiteralPath $data.Destination -Force }
            Write-UpdateLog 'Previous EXE restored.'
        } catch { Write-UpdateLog ('Rollback failed: '+$_) }
    }
    if($startupChanged) {
        try {
            $startup=Get-ItemProperty -LiteralPath $data.StartupKey -Name $data.StartupName -ErrorAction SilentlyContinue
            if($null -ne $startup -and $startup.($data.StartupName) -eq $data.NewStartupCommand) {
                Set-ItemProperty -LiteralPath $data.StartupKey -Name $data.StartupName -Value $data.OldStartupCommand
            }
        } catch { Write-UpdateLog ('Startup rollback failed: '+$_) }
    }
    try { Start-Process -FilePath $data.Target -WorkingDirectory (Split-Path -Parent $data.Target) -WindowStyle Normal } catch { Write-UpdateLog ('Restart failed: '+$_) }
} finally {
    if(Test-Path -LiteralPath $next){Remove-Item -LiteralPath $next -Force}
}
""";
}
