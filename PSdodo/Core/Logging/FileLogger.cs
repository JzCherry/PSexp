using System;
using System.IO;

namespace PsExporter.Core.Logging;

/// <summary>
/// 默认将运行日志写入当前用户的本地数据目录。
/// </summary>
public sealed class FileLogger
{
    private static readonly object _lock = new();
    private readonly string _logPath;

    public string LogPath => _logPath;

    public FileLogger() : this(Path.Combine(AppProfile.LocalDirectory, "Logs", "psx.log"))
    {
    }

    public FileLogger(string logPath)
    {
        _logPath = logPath;
        try
        {
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* ignore */ }
    }

    public void Log(string level, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    public void Info(string msg) => Log("Info", msg);
    public void Warn(string msg) => Log("Warn", msg);
    public void Error(string msg) => Log("Error", msg);
    public void Debug(string msg) => Log("Debug", msg);

    /// <summary>读取当前日志文件的全部内容（供开发者外部查看）</summary>
    public string ReadAll()
    {
        try
        {
            lock (_lock)
            {
                return File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;
            }
        }
        catch { return string.Empty; }
    }

    /// <summary>清空日志文件</summary>
    public void Clear()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(_logPath)) File.WriteAllText(_logPath, string.Empty);
            }
        }
        catch { /* ignore */ }
    }
}
