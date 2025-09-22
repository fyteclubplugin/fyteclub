using System.Text.RegularExpressions;

namespace FyteClub.ModSystem.Advanced;

public partial class FileReplacementData
{
    public FileReplacementData(string[] gamePaths, string filePath)
    {
        GamePaths = gamePaths.Select(g => g.Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        ResolvedPath = filePath.Replace('\\', '/');
    }

    public HashSet<string> GamePaths { get; init; }
    public string Hash { get; set; } = string.Empty;
    public string ResolvedPath { get; init; }

    public bool HasFileReplacement => GamePaths.Count >= 1 && GamePaths.Any(p => !string.Equals(p, ResolvedPath, StringComparison.Ordinal));
    public bool IsFileSwap => !LocalPathRegex().IsMatch(ResolvedPath) && GamePaths.All(p => !LocalPathRegex().IsMatch(p));

    [GeneratedRegex(@"^[a-zA-Z]:(/|\\)", RegexOptions.ECMAScript)]
    private static partial Regex LocalPathRegex();

    public override string ToString()
    {
        return $"HasReplacement:{HasFileReplacement},IsFileSwap:{IsFileSwap} - {string.Join(",", GamePaths)} => {ResolvedPath}";
    }
}