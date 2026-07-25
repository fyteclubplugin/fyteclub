using System;

namespace FyteClub.ModSync.Protocol
{
    /// <summary>
    /// Represents a file that can be transferred over P2P
    /// </summary>
    public class TransferableFile
    {
        public string GamePath { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public long Size { get; set; }
    }
}
