using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public static class SecureLogger
    {
        private static readonly Regex LogSanitizer = new(@"[\r\n\t]", RegexOptions.Compiled);
        private static IPluginLog? _pluginLog;
        
        public static void Initialize(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }
        
        public static void LogInfo(string message, params object[] args)
        {
            // Skip excessive transfer logging
            if (message.Contains("📨📨📨") || message.Contains("received mod data from syncshell"))
                return;
                
            var sanitizedMessage = SanitizeLogInput(message);
            var sanitizedArgs = args?.Select(arg => SanitizeLogInput(arg?.ToString() ?? "")).ToArray() ?? new string[0];
            
            try
            {
                var formatted = string.Format(sanitizedMessage, sanitizedArgs);
                _pluginLog?.Info($"[SECURE] {formatted}");
            }
            catch (FormatException)
            {
                _pluginLog?.Info($"[SECURE] {sanitizedMessage}");
            }
        }
        
        public static void LogWarning(string message, params object[] args)
        {
            var sanitizedMessage = SanitizeLogInput(message);
            var sanitizedArgs = args?.Select(arg => SanitizeLogInput(arg?.ToString() ?? "")).ToArray() ?? new string[0];
            
            try
            {
                var formatted = string.Format(sanitizedMessage, sanitizedArgs);
                _pluginLog?.Warning($"[SECURE] {formatted}");
            }
            catch (FormatException)
            {
                _pluginLog?.Warning($"[SECURE] {sanitizedMessage}");
            }
        }
        
        public static void LogError(string message, params object[] args)
        {
            var sanitizedMessage = SanitizeLogInput(message);
            var sanitizedArgs = args?.Select(arg => SanitizeLogInput(arg?.ToString() ?? "")).ToArray() ?? new string[0];
            
            try
            {
                var formatted = string.Format(sanitizedMessage, sanitizedArgs);
                _pluginLog?.Error($"[SECURE] {formatted}");
            }
            catch (FormatException)
            {
                _pluginLog?.Error($"[SECURE] {sanitizedMessage}");
            }
        }
        
        public static void LogDebug(string message, params object[] args)
        {
            var sanitizedMessage = SanitizeLogInput(message);
            var sanitizedArgs = args?.Select(arg => SanitizeLogInput(arg?.ToString() ?? "")).ToArray() ?? new string[0];
            
            try
            {
                var formatted = string.Format(sanitizedMessage, sanitizedArgs);
                _pluginLog?.Debug($"[SECURE] {formatted}");
            }
            catch (FormatException)
            {
                _pluginLog?.Debug($"[SECURE] {sanitizedMessage}");
            }
        }
        
        private static string SanitizeLogInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            
            // Remove control characters that could be used for log injection
            var sanitized = LogSanitizer.Replace(input, "_");
            
            // Truncate to prevent log flooding
            if (sanitized.Length > 500)
                sanitized = sanitized.Substring(0, 497) + "...";
                
            return sanitized;
        }
    }
}