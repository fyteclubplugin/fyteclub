namespace FyteClub.Networking
{
    /// <summary>
    /// TURN/ICE relay server descriptor. Populated from user-supplied configuration or
    /// invite metadata — see docs/PLAN.md AD-1 for the planned configurable TURN support.
    /// </summary>
    public class TurnServerInfo
    {
        public string Url { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string HostPlayerId { get; set; } = "";
        public string SyncshellId { get; set; } = "";
        public int UserCount { get; set; } = 0;
    }
}
