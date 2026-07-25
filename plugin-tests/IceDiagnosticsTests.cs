#nullable enable
using System.Collections.Generic;
using Xunit;
using FyteClub.Networking;

namespace FyteClub.Tests
{
    /// <summary>
    /// Unit tests for docs/PLAN.md Phase 4 item 2 (ICE failure diagnosis): WebRTCManager's pure
    /// diagnostic-message builder. Deliberately doesn't stand up a real PeerConnection - this
    /// covers only the message-selection logic, which is what actually encodes the diagnosis
    /// (missing candidates -> firewall guess, no relay + no TURN -> "add a TURN server", etc.).
    /// </summary>
    public class IceDiagnosticsTests
    {
        [Fact]
        public void Connected_ViaRelay_MentionsTurnRelay()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Connected, new List<string> { "host", "srflx", "relay" }, turnConfigured: true);

            Assert.Contains("TURN relay", message);
        }

        [Fact]
        public void Connected_ViaSrflxOnly_MentionsStunReflexive()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Connected, new List<string> { "host", "srflx" }, turnConfigured: false);

            Assert.Contains("STUN-reflexive", message);
        }

        [Fact]
        public void Connected_ViaHostOnly_MentionsDirect()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Connected, new List<string> { "host" }, turnConfigured: false);

            Assert.Contains("a direct candidate", message);
        }

        [Fact]
        public void Failed_NoCandidatesGathered_SuggestsFirewallOrNetwork()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Failed, new List<string>(), turnConfigured: false);

            Assert.Contains("No ICE candidates were gathered at all", message);
        }

        [Fact]
        public void Failed_OnlyHostSrflx_NoTurnConfigured_SuggestsAddingTurnServer()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Failed, new List<string> { "host", "srflx" }, turnConfigured: false);

            Assert.Contains("no TURN relay available", message);
            Assert.Contains("Network tab", message);
        }

        [Fact]
        public void Failed_NoRelay_TurnConfigured_SuggestsCheckingCredentials()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Failed, new List<string> { "host", "srflx" }, turnConfigured: true);

            Assert.Contains("check the TURN server URL and credentials", message);
        }

        [Fact]
        public void Failed_RelayGathered_SuggestsPeerOffline()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Failed, new List<string> { "host", "srflx", "relay" }, turnConfigured: true);

            Assert.Contains("connection still failed", message);
            Assert.Contains("may be offline", message);
        }

        [Fact]
        public void Disconnected_TreatedSameAsFailed_ForDiagnosis()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Disconnected, new List<string>(), turnConfigured: false);

            Assert.Contains("No ICE candidates were gathered at all", message);
        }

        [Fact]
        public void Gathering_WithNoCandidatesYet_ShowsGenericGatheringMessage()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Checking, new List<string>(), turnConfigured: false);

            Assert.Equal("Gathering ICE candidates...", message);
        }

        [Fact]
        public void Gathering_WithSomeCandidates_ListsThemSoFar()
        {
            var message = WebRTCManager.BuildDiagnosticMessage(
                ConnectionDiagnosticState.Checking, new List<string> { "host" }, turnConfigured: false);

            Assert.Contains("host so far", message);
        }
    }
}
