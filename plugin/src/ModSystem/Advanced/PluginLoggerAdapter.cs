using System;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace FyteClub.ModSystem.Advanced;

public class PluginLoggerAdapter<T> : ILogger<T>
{
    private readonly IPluginLog _pluginLog;

    public PluginLoggerAdapter(IPluginLog pluginLog)
    {
        _pluginLog = pluginLog;
    }

    IDisposable ILogger.BeginScope<TState>(TState state) => null!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        
        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                _pluginLog.Debug(message);
                break;
            case LogLevel.Information:
                _pluginLog.Info(message);
                break;
            case LogLevel.Warning:
                _pluginLog.Warning(message);
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                _pluginLog.Error(exception, message);
                break;
        }
    }
}