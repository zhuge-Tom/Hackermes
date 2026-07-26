using System;

namespace Hookmes.Base.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// 轻量日志抽象。统一入口,避免诊断输出散落成各处的 <c>Debug.WriteLine</c>。
/// 一条原则:<strong>日志失败绝不能影响应用行为</strong>,实现方需吞掉自身异常。
/// </summary>
public interface IAppLogger
{
    void Log(LogLevel level, string category, string message, Exception? exception = null);
}

public static class AppLoggerExtensions
{
    public static void Debug(this IAppLogger logger, string message) =>
        logger.Log(LogLevel.Debug, string.Empty, message);

    public static void Info(this IAppLogger logger, string message) =>
        logger.Log(LogLevel.Info, string.Empty, message);

    public static void Warn(this IAppLogger logger, string message) =>
        logger.Log(LogLevel.Warn, string.Empty, message);

    public static void Error(this IAppLogger logger, string message, Exception? exception = null) =>
        logger.Log(LogLevel.Error, string.Empty, message, exception);

    /// <summary>返回一个固定 category 的包装器,免去每次传模块名。</summary>
    public static IAppLogger ForCategory(this IAppLogger logger, string category) =>
        new CategoryLogger(logger, category);

    private sealed class CategoryLogger(IAppLogger inner, string category) : IAppLogger
    {
        public void Log(LogLevel level, string _, string message, Exception? exception = null) =>
            inner.Log(level, category, message, exception);
    }
}
