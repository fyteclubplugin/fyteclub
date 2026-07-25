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
}
