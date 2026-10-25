using Microsoft.Extensions.Logging;
using Strands.Agents.Logging;

namespace Strands.Harness;

/// <summary>Configures the harness's process-wide standard logger independently of the core SDK logger.</summary>
public static class HarnessLogging
{
    private static readonly LogDispatcher Dispatcher = new();
    /// <summary>The category requested from a supplied logger factory.</summary>
    public const string CategoryName = "Strands.Harness";
    /// <summary>The currently borrowed logger. Retain this reference to restore an earlier configuration.</summary>
    public static ILogger Logger => Dispatcher.Logger;
    /// <summary>Atomically replaces the harness logger without disposing application logging resources.</summary>
    /// <param name="logger">The application logger.</param>
    public static void ConfigureLogging(ILogger logger)
    {
        Guard.NotNull(logger, nameof(logger));
        Dispatcher.Configure(logger);
    }
    /// <summary>Creates the harness category using a borrowed application logger factory.</summary>
    /// <param name="factory">The application's logger factory, which remains caller-owned.</param>
    public static void ConfigureLogging(ILoggerFactory factory)
    {
        Guard.NotNull(factory, nameof(factory));
        ConfigureLogging(factory.CreateLogger(CategoryName));
    }

    internal static void Warning(string template, params object?[] values) => Dispatcher.Write(LogLevel.Warning, null, template, values);
    internal static void Warning(Exception error, string template, params object?[] values) => Dispatcher.Write(LogLevel.Warning, error, template, values);
    internal static void Information(string template, params object?[] values) => Dispatcher.Write(LogLevel.Information, null, template, values);
    internal static void WarnOnce(string message) => Dispatcher.WarnOnce(message);
}
