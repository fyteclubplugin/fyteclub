using System.Collections.Generic;

namespace FyteClub.Networking
{
    public enum ConnectionDiagnosticState
    {
        Unknown,
        Gathering,
        Checking,
        Connected,
        Failed,
        Disconnected
    }

    /// <summary>
    /// Human-facing snapshot of the last ICE connection attempt for a peer: which local
    /// candidate types were gathered (host/srflx/relay), whether a TURN server was configured,
    /// and a plain-English summary of what's actually going on. Deliberately free of
    /// Microsoft.MixedReality.WebRTC types - docs/PLAN.md Phase 3 item 6 keeps those contained
    /// to the Transport layer (WebRTCManager/Peer), so this crosses into SyncshellManager/UI as
    /// a plain DTO.
    /// </summary>
    public class IceDiagnostics
    {
        public ConnectionDiagnosticState State { get; set; } = ConnectionDiagnosticState.Unknown;
        public List<string> LocalCandidateTypes { get; set; } = new();
        public bool TurnConfigured { get; set; }
        public string Message { get; set; } = "";
    }
}
