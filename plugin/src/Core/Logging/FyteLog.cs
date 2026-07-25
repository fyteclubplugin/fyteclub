using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace FyteClub.Core.Logging
{
    /// <summary>
    /// Single logging facade for the plugin, replacing the former ModularLogger/SecureLogger split.
    /// Error/Warn/Info always emit (matches the previous "Always" visibility); Debug is gated by
    /// the per-module toggle + debug switch in LoggingManager, unchanged from before.
    ///
    /// Every message passes through emoji stripping and secret redaction here, so migrated call
    /// sites get clean output even where the source string literal itself hasn't been hand-cleaned.
    /// </summary>
    public static class FyteLog
    {
        private static IPluginLog? _pluginLog;

        private static readonly Regex ControlCharSanitizer = new(@"[\r\n\t]", RegexOptions.Compiled);
        // Emoji / pictographic / symbol ranges commonly found in this codebase's log strings.
        private static readonly Regex EmojiSanitizer = new(
            @"[←-⇿⌀-➿⬀-⯿\uD83C-􏰀-\uDFFF️]+\s*",
            RegexOptions.Compiled);
        private static readonly Regex SecretSanitizer = new(
            @"(?i)\b(password|passwd|secret|token|apikey|api_key|encryptionkey|privatekey)\b\s*[=:]\s*\S+",
            RegexOptions.Compiled);

        private const int MaxMessageLength = 1000;

        public static void Initialize(IPluginLog pluginLog) => _pluginLog = pluginLog;

        public static void Error(LogModule module, string message) => Write(LogLevel.Always, module, message, Severity.Error);
        public static void Error(LogModule module, string format, params object[] args) => Write(LogLevel.Always, module, SafeFormat(format, args), Severity.Error);

        public static void Warn(LogModule module, string message) => Write(LogLevel.Always, module, message, Severity.Warn);
        public static void Warn(LogModule module, string format, params object[] args) => Write(LogLevel.Always, module, SafeFormat(format, args), Severity.Warn);

        public static void Info(LogModule module, string message) => Write(LogLevel.Always, module, message, Severity.Info);
        public static void Info(LogModule module, string format, params object[] args) => Write(LogLevel.Always, module, SafeFormat(format, args), Severity.Info);

        public static void Debug(LogModule module, string message) => Write(LogLevel.Debug, module, message, Severity.Debug);
        public static void Debug(LogModule module, string format, params object[] args) => Write(LogLevel.Debug, module, SafeFormat(format, args), Severity.Debug);

        private enum Severity { Error, Warn, Info, Debug }

        private static string SafeFormat(string format, object[] args)
        {
            if (args == null || args.Length == 0) return format;
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        private static void Write(LogLevel gate, LogModule module, string message, Severity severity)
        {
            if (!LoggingManager.ShouldLog(gate, module)) return;
            if (_pluginLog == null) return;

            var clean = Sanitize(message);
            var tagged = $"[{module}] {clean}";

            switch (severity)
            {
                case Severity.Error: _pluginLog.Error(tagged); break;
                case Severity.Warn: _pluginLog.Warning(tagged); break;
                case Severity.Info: _pluginLog.Info(tagged); break;
                case Severity.Debug: _pluginLog.Debug(tagged); break;
            }
        }

        private static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var sanitized = ControlCharSanitizer.Replace(input, "_");
            sanitized = EmojiSanitizer.Replace(sanitized, "");
            sanitized = SecretSanitizer.Replace(sanitized, m =>
            {
                var eqIdx = m.Value.IndexOfAny(new[] { '=', ':' });
                var name = eqIdx > 0 ? m.Value[..eqIdx].Trim() : m.Value;
                return $"{name}=***REDACTED***";
            });

            if (sanitized.Length > MaxMessageLength)
                sanitized = sanitized[..(MaxMessageLength - 3)] + "...";

            return sanitized;
        }
    }
}
