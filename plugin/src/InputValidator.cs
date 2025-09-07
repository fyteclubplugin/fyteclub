using System;
using System.Text.RegularExpressions;

namespace FyteClub
{
    public static class InputValidator
    {
        private static readonly Regex SyncshellNameRegex = new(@"^[a-zA-Z0-9\s\-_.]{1,50}$", RegexOptions.Compiled);
        private static readonly Regex PlayerNameRegex = new(@"^[a-zA-Z0-9\s\-_.@]{1,32}$", RegexOptions.Compiled);
        
        public static bool IsValidSyncshellName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return SyncshellNameRegex.IsMatch(name);
        }
        
        public static bool IsValidPlayerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return PlayerNameRegex.IsMatch(name);
        }
        
        public static string SanitizeInput(string input, int maxLength = 100)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            
            // Remove control characters and limit length
            var sanitized = Regex.Replace(input, @"[\x00-\x1F\x7F]", "");
            
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength);
                
            return sanitized;
        }
        
        public static bool IsValidSyncshellId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            return Regex.IsMatch(id, @"^[a-fA-F0-9]{32,64}$");
        }
        
        public static string SanitizeForHtml(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
        }
        
        public static string SanitizeForLog(string input)
        {
            return SanitizeInput(input, 200);
        }
        
        public static bool ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return Uri.TryCreate(url, UriKind.Absolute, out var result) && 
                   (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
        }
    }
}