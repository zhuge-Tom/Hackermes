using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Hackermes.Base.Diagnostics;

/// <summary>
/// 写 <c>%LocalAppData%\Hackermes\logs\latest.log</c> 的最小实现。
/// 所有 IO 异常一律吞掉 —— 诊断设施永远不能成为故障源。
/// </summary>
public sealed class FileAppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly string? _logPath;
    private readonly LogLevel _minLevel;

    public FileAppLogger(LogLevel minLevel = LogLevel.Info)
    {
        _minLevel = minLevel;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hackermes",
                "logs");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "latest.log");

            // 每次启动截断,避免无限增长;上一次的内容滚到 previous.log。
            if (File.Exists(_logPath))
                File.Move(_logPath, Path.Combine(dir, "previous.log"), overwrite: true);
        }
        catch
        {
            _logPath = null;
        }
    }

    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minLevel)
            return;

        var line = Format(level, category, message, exception);
        Debug.WriteLine(line);

        if (_logPath is null)
            return;

        try
        {
            lock (_gate)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 有意吞掉。
        }
    }

    private static string Format(LogLevel level, string category, string message, Exception? exception)
    {
        var sb = new StringBuilder(128);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5));
        if (!string.IsNullOrEmpty(category))
            sb.Append(" [").Append(category).Append(']');
        sb.Append(' ').Append(message);
        if (exception is not null)
            sb.Append(Environment.NewLine).Append(exception);
        return sb.ToString();
    }
}
