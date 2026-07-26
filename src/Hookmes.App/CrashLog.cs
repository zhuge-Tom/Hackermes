using System;
using System.IO;
using System.Text;

namespace Hookmes.App;

/// <summary>
/// 崩溃日志。故意不依赖 DI —— 容器构建失败时它也必须能工作。
/// 所有 IO 异常吞掉:诊断设施永远不能成为新的故障源。
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    public static void Write(string source, Exception? exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Hookmes",
                "logs");
            Directory.CreateDirectory(dir);

            var text = new StringBuilder()
                .AppendLine("──────────────────────────────────────────")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").AppendLine(source)
                .AppendLine(exception?.ToString() ?? "(无异常对象)")
                .ToString();

            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, "crash.log"), text, Encoding.UTF8);
            }
        }
        catch
        {
            // 有意吞掉。
        }
    }
}
