using Dalamud.Plugin.Services;
using Serilog;
using Serilog.Events;
using System;

namespace FyteClub
{
    public class MockPluginLog : IPluginLog
    {
        public ILogger Logger { get; } = new LoggerConfiguration().CreateLogger();
        public LogEventLevel MinimumLogLevel { get; set; } = LogEventLevel.Debug;
        
        public void Debug(string messageTemplate, params object[] values) => SecureLogger.LogInfo(messageTemplate, values);
        public void Debug(Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogError("{0}: {1}", exception?.Message ?? "Unknown", string.Format(messageTemplate, values));
        public void Error(string messageTemplate, params object[] values) => SecureLogger.LogError(messageTemplate, values);
        public void Error(Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogError("{0}: {1}", exception?.Message ?? "Unknown", string.Format(messageTemplate, values));
        public void Fatal(string messageTemplate, params object[] values) => SecureLogger.LogError("FATAL: {0}", string.Format(messageTemplate, values));
        public void Fatal(Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogError("FATAL {0}: {1}", exception?.Message ?? "Unknown", string.Format(messageTemplate, values));
        public void Info(string messageTemplate, params object[] values) => SecureLogger.LogInfo(messageTemplate, values);
        public void Info(Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogInfo("{0}: {1}", exception?.Message ?? "Unknown", string.Format(messageTemplate, values));
        public void Information(string messageTemplate, params object[] values) => SecureLogger.LogInfo(messageTemplate, values);
        public void Information(Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogInfo("{0}: {1}", exception?.Message ?? "Unknown", string.Format(messageTemplate, values));
        public void Verbose(string messageTemplate, params object[] values) => SecureLogger.LogInfo(messageTemplate, values);
        public void Verbose(Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogInfo("{0}: {1}", exception?.Message ?? "Unknown", string.Format(messageTemplate, values));
        public void Warning(string messageTemplate, params object[] values) => SecureLogger.LogWarning(messageTemplate, values);
        public void Warning(Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogWarning("{0}: {1}", exception?.Message ?? "Unknown", string.Format(messageTemplate, values));
        public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values) => SecureLogger.LogInfo("{0}: {1}", level, string.Format(messageTemplate, values));
    }
}