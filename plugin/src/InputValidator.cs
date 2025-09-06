using System;
using System.Text.RegularExpressions;
using System.Web;

namespace FyteClub
{
    public static class InputValidator
    {
        private static readonly Regex SafeStringPattern = new(@"^[a-zA-Z0-9\-_\.]+$", RegexOptions.Compiled);
        private static readonly Regex InviteCodePattern = new(@"^[a-zA-Z0-9\-_=]+$", RegexOptions.Compiled);
        
        public static string SanitizeForLog(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            
            // Remove control characters and limit length
            var sanitized = Regex.Replace(input, @"[\r\n\t\x00-\x1F\x7F]", "_");
            return sanitized.Length > 200 ? sanitized.Substring(0, 197) + "..." : sanitized;
        }
        
        public static string SanitizeForHtml(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return HttpUtility.HtmlEncode(input);
        }
        
        public static bool IsValidSyncshellName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && 
                   name.Length <= 50 && 
                   SafeStringPattern.IsMatch(name);
        }
        
        public static bool IsValidInviteCode(string code)
        {
            return !string.IsNullOrWhiteSpace(code) && 
                   code.Length <= 1000 && 
                   InviteCodePattern.IsMatch(code);
        }
        
        public static string ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) 
                throw new ArgumentException("URL cannot be empty");
                
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new ArgumentException("Invalid URL format");
                
            if (uri.Scheme != "https")
                throw new ArgumentException("Only HTTPS URLs are allowed");
                
            return uri.ToString();
        }
    }
}