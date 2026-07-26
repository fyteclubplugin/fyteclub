#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using FyteClub.ModSync.Application;

namespace FyteClub.Tests
{
    /// <summary>
    /// Unit tests for the live-bug fix found during the first in-game smoke test (docs/PLAN.md):
    /// when Penumbra reports more than one actual-file candidate for a single game path (seen
    /// with multi-race body mods keeping several race options active at once),
    /// FyteClubModIntegration.ProcessFileReplacementsAsync used to pick via HashSet.FirstOrDefault -
    /// an arbitrary, unordered choice that could hand out a different race's texture for the same
    /// game path. SelectBestReplacementCandidate is the pulled-out selection logic: prefer whichever
    /// candidate actually exists on disk, in order, falling back to the first if none resolve.
    /// Uses real temp files rather than a mocked filesystem since the original bug was specifically
    /// about a real File.Exists check.
    /// </summary>
    public class ModReplacementSelectionTests : IDisposable
    {
        private readonly string _tempDir;

        public ModReplacementSelectionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "FyteClubModReplacementSelectionTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
        }

        private string CreateRealFile(string name)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, "test content");
            return path;
        }

        private string NonExistentPath(string name) => Path.Combine(_tempDir, name);

        [Fact]
        public void SingleCandidate_ReturnsIt_RegardlessOfExistence()
        {
            var missing = NonExistentPath("missing.tex");
            var result = FyteClubModIntegration.SelectBestReplacementCandidate(
                new List<(string, string)> { ("gamepath/a.tex", missing) });

            Assert.Equal(missing, result.ResolvedPath);
        }

        [Fact]
        public void MultipleCandidates_PrefersTheOneThatExistsOnDisk()
        {
            var missing = NonExistentPath("highlander_missing.tex");
            var real = CreateRealFile("viera_real.tex");

            // The bug: a naive first/arbitrary pick could return the missing (or wrong-race) one
            // even though a later candidate in the set actually resolves.
            var result = FyteClubModIntegration.SelectBestReplacementCandidate(
                new List<(string, string)> { ("gamepath/body.tex", missing), ("gamepath/body.tex", real) });

            Assert.Equal(real, result.ResolvedPath);
        }

        [Fact]
        public void MultipleCandidates_FirstExistingWins_WhenSeveralExist()
        {
            var firstReal = CreateRealFile("first.tex");
            var secondReal = CreateRealFile("second.tex");

            var result = FyteClubModIntegration.SelectBestReplacementCandidate(
                new List<(string, string)> { ("gamepath/body.tex", firstReal), ("gamepath/body.tex", secondReal) });

            Assert.Equal(firstReal, result.ResolvedPath);
        }

        [Fact]
        public void MultipleCandidates_NoneExist_FallsBackToFirst()
        {
            var missingA = NonExistentPath("missing_a.tex");
            var missingB = NonExistentPath("missing_b.tex");

            var result = FyteClubModIntegration.SelectBestReplacementCandidate(
                new List<(string, string)> { ("gamepath/body.tex", missingA), ("gamepath/body.tex", missingB) });

            Assert.Equal(missingA, result.ResolvedPath);
        }

        [Fact]
        public void MultipleCandidates_PreservesReplacementPathPairedWithChosenResolvedPath()
        {
            var real = CreateRealFile("real.tex");
            var result = FyteClubModIntegration.SelectBestReplacementCandidate(
                new List<(string, string)>
                {
                    ("original/relative/missing.tex", NonExistentPath("missing.tex")),
                    ("original/relative/real.tex", real)
                });

            Assert.Equal("original/relative/real.tex", result.ReplacementPath);
            Assert.Equal(real, result.ResolvedPath);
        }
    }

    /// <summary>
    /// Regression tests for a deeper bug found while investigating why Chaos mode stopped
    /// finding any mods at all after the SelectBestReplacementCandidate fix shipped: Penumbra's
    /// GetGameObjectResourcePaths/GetPlayerResourcePaths returns
    /// Dictionary&lt;ActualPath, HashSet&lt;GamePath&gt;&gt; (confirmed against the Penumbra.Api
    /// XML docs: "dictionaries of actual paths ... to game paths") - keyed by the real/modded
    /// file, valued by the vanilla game paths it replaces. FyteClubModIntegration read this
    /// backwards for as long as the method existed, treating the actual-path key as the game
    /// path and the game-path values as candidate replacement files. Every "candidate" produced
    /// that way is a short relative game-path string, which File.Exists can never resolve -
    /// previously invisible because the old fallback shipped the broken entry anyway, and only
    /// surfaced as "0 mods found" once the drop-if-unresolvable fix stopped doing that.
    /// </summary>
    public class ModResourcePathInversionTests
    {
        [Fact]
        public void SingleActualFile_MapsToItsGamePath()
        {
            // Real shape: one modded file (key) redirects one vanilla game path (value).
            var resourcePaths = new Dictionary<string, HashSet<string>>
            {
                [@"C:\Users\Me\Documents\penumbra\MyMod\chara\human\c0301\obj\body\b0001\texture\--c0301b0001_d.tex"]
                    = new() { "chara/human/c0301/obj/body/b0001/texture/--c0301b0001_d.tex" }
            };

            var result = FyteClubModIntegration.InvertActualPathToGamePaths(resourcePaths);

            Assert.Single(result);
            var gamePath = "chara/human/c0301/obj/body/b0001/texture/--c0301b0001_d.tex";
            Assert.True(result.ContainsKey(gamePath));
            Assert.Equal(@"C:\Users\Me\Documents\penumbra\MyMod\chara\human\c0301\obj\body\b0001\texture\--c0301b0001_d.tex", result[gamePath][0]);
        }

        [Fact]
        public void VanillaEntry_WhereKeyEqualsValue_IsNotTreatedAsAReplacement()
        {
            // Penumbra includes unmodified resources too, where the "actual path" is just the
            // game path itself - not a real redirect, must be excluded.
            var resourcePaths = new Dictionary<string, HashSet<string>>
            {
                ["chara/human/c0301/obj/body/b0001/texture/vanilla.tex"]
                    = new() { "chara/human/c0301/obj/body/b0001/texture/vanilla.tex" }
            };

            var result = FyteClubModIntegration.InvertActualPathToGamePaths(resourcePaths);

            Assert.Empty(result);
        }

        [Fact]
        public void OneActualFile_ReplacingMultipleGamePaths_ProducesOneEntryPerGamePath()
        {
            // A single texture commonly backs more than one game path (e.g. base + overlay slots).
            var sharedFile = @"C:\Users\Me\Documents\penumbra\MyMod\shared.tex";
            var resourcePaths = new Dictionary<string, HashSet<string>>
            {
                [sharedFile] = new() { "chara/slot_a/texture.tex", "chara/slot_b/texture.tex" }
            };

            var result = FyteClubModIntegration.InvertActualPathToGamePaths(resourcePaths);

            Assert.Equal(2, result.Count);
            Assert.Equal(sharedFile, result["chara/slot_a/texture.tex"][0]);
            Assert.Equal(sharedFile, result["chara/slot_b/texture.tex"][0]);
        }

        [Fact]
        public void MultipleActualFiles_ClaimingSameGamePath_AreGroupedAsCandidates()
        {
            // The scenario that originally looked like "multi-race body mod ambiguity": two
            // different actual files both list the same game path, so both must appear as
            // candidates for that single game path (for SelectBestReplacementCandidate to pick
            // between), rather than being treated as two separate unrelated game paths.
            var highlanderFile = @"C:\Users\Me\Documents\penumbra\The_Body_SE\Step 3 - Highlander\T0\body_d.tex";
            var vieraFile = @"C:\Users\Me\Documents\penumbra\The_Body_SE\Step 5 - Viera\T0\body_d.tex";
            var gamePath = "chara/human/c0301/obj/body/b0001/texture/--c0301b0001_d.tex";

            var resourcePaths = new Dictionary<string, HashSet<string>>
            {
                [highlanderFile] = new() { gamePath },
                [vieraFile] = new() { gamePath }
            };

            var result = FyteClubModIntegration.InvertActualPathToGamePaths(resourcePaths);

            Assert.Single(result);
            Assert.Equal(2, result[gamePath].Count);
            Assert.Contains(highlanderFile, result[gamePath]);
            Assert.Contains(vieraFile, result[gamePath]);
        }

        [Fact]
        public void EmptyInput_ProducesEmptyOutput()
        {
            var result = FyteClubModIntegration.InvertActualPathToGamePaths(new Dictionary<string, HashSet<string>>());
            Assert.Empty(result);
        }

        [Fact]
        public void NullValueSet_IsSkippedWithoutThrowing()
        {
            var resourcePaths = new Dictionary<string, HashSet<string>>
            {
                ["some/actual/path.tex"] = null!
            };

            var result = FyteClubModIntegration.InvertActualPathToGamePaths(resourcePaths);

            Assert.Empty(result);
        }
    }
}
