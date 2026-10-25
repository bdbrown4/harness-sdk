using Microsoft.Extensions.Logging;
using Strands.Agents.Logging;

namespace Strands.Agents;

/// <summary>Configures the SDK's process-wide standard .NET logger independently of the harness logger.</summary>
public static class SdkLogging
{
    private static readonly LogDispatcher Dispatcher = new();
    /// <summary>The category used when configuring a borrowed logger factory.</summary>
    public const string CategoryName = "Strands.Agents";
    /// <summary>The currently borrowed logger. Retain this reference to restore an earlier configuration.</summary>
    public static ILogger Logger => Dispatcher.Logger;
    /// <summary>Atomically replaces the SDK logger without disposing the previous or supplied logger.</summary>
    /// <param name="logger">The application's logger.</param>
    public static void ConfigureLogging(ILogger logger)
    {
        Guard.NotNull(logger, nameof(logger));
        Dispatcher.Configure(logger);
    }
    /// <summary>Creates the SDK category through a borrowed application logger factory.</summary>
    /// <param name="factory">The application's logger factory, which remains caller-owned.</param>
    public static void ConfigureLogging(ILoggerFactory factory)
    {
        Guard.NotNull(factory, nameof(factory));
        ConfigureLogging(factory.CreateLogger(CategoryName));
    }

    internal static void Warning(string template, params object?[] values) => Dispatcher.Write(LogLevel.Warning, null, template, values);
    internal static void Warning(Exception error, string template, params object?[] values) => Dispatcher.Write(LogLevel.Warning, error, template, values);
    internal static Exception? WarningFailure(Exception error, string template, params object?[] values)
    {
        Dispatcher.Write(LogLevel.Warning, error, template, values, out var observerFailure);
        return observerFailure;
    }
    internal static void Information(string template, params object?[] values) => Dispatcher.Write(LogLevel.Information, null, template, values);
    internal static void Information(Exception error, string template, params object?[] values) => Dispatcher.Write(LogLevel.Information, error, template, values);
    internal static void Error(Exception error, string template, params object?[] values) => Dispatcher.Write(LogLevel.Error, error, template, values);
    internal static void WarnOnce(string message) => Dispatcher.WarnOnce(message);
}
