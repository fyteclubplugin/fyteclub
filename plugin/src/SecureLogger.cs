using System;
using System.Text.RegularExpressions;

namespace FyteClub
{
    public static class SecureLogger
    {
        private static readonly Regex LogSanitizer = new(@"[\r\n\t]", RegexOptions.Compiled);
        
        public static void LogInfo(string message, params object[] args)
        {
            var sanitized = SanitizeLogInput(string.Format(message, args));
            Console.WriteLine($"[INFO] {sanitized}");
        }
        
        public static void LogWarning(string message, params object[] args)
        {
            var sanitized = SanitizeLogInput(string.Format(message, args));
            Console.WriteLine($"[WARN] {sanitized}");
        }
        
        public static void LogError(string message, params object[] args)
        {
            var sanitized = SanitizeLogInput(string.Format(message, args));
            Console.WriteLine($"[ERROR] {sanitized}");
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