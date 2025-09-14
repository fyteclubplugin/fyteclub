using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace FyteClub
{
    public static class SecureLogger
    {
        private static readonly Regex LogSanitizer = new(@"[\r\n\t]", RegexOptions.Compiled);
        
        public static void LogInfo(string message, params object[] args)
        {
            var sanitizedMessage = SanitizeLogInput(message);
            var sanitizedArgs = args?.Select(arg => SanitizeLogInput(arg?.ToString() ?? "")).ToArray() ?? new string[0];
            
            try
            {
                var formatted = string.Format(sanitizedMessage, sanitizedArgs);
                Console.WriteLine($"[INFO] {formatted}");
            }
            catch (FormatException)
            {
                // Fallback if format string is invalid
                Console.WriteLine($"[INFO] {sanitizedMessage}");
            }
        }
        
        public static void LogWarning(string message, params object[] args)
        {
            var sanitizedMessage = SanitizeLogInput(message);
            var sanitizedArgs = args?.Select(arg => SanitizeLogInput(arg?.ToString() ?? "")).ToArray() ?? new string[0];
            
            try
            {
                var formatted = string.Format(sanitizedMessage, sanitizedArgs);
                Console.WriteLine($"[WARN] {formatted}");
            }
            catch (FormatException)
            {
                // Fallback if format string is invalid
                Console.WriteLine($"[WARN] {sanitizedMessage}");
            }
        }
        
        public static void LogError(string message, params object[] args)
        {
            var sanitizedMessage = SanitizeLogInput(message);
            var sanitizedArgs = args?.Select(arg => SanitizeLogInput(arg?.ToString() ?? "")).ToArray() ?? new string[0];
            
            try
            {
                var formatted = string.Format(sanitizedMessage, sanitizedArgs);
                Console.WriteLine($"[ERROR] {formatted}");
            }
            catch (FormatException)
            {
                // Fallback if format string is invalid
                Console.WriteLine($"[ERROR] {sanitizedMessage}");
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