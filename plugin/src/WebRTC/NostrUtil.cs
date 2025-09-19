using System;
using System.Collections.Generic;
using System.Linq;

namespace FyteClub.WebRTC
{
    public static class NostrUtil
    {
        // Ephemeral keys placeholder (NOT bech32). In production, use a proper Nostr key lib.
        public static (string priv, string pub) GenerateEphemeralKeys()
        {
            // Just random hex for now; replace with real secp256k1 key handling
            var rng = new Random();
            byte[] priv = new byte[32];
            rng.NextBytes(priv);
            var privHex = BitConverter.ToString(priv).Replace("-", "").ToLowerInvariant();
            // Fake pub key (same format). Real impl derives pub from priv.
            byte[] pub = new byte[32];
            rng.NextBytes(pub);
            var pubHex = BitConverter.ToString(pub).Replace("-", "").ToLowerInvariant();
            return (privHex, pubHex);
        }

        public static (string uuid, string[] relays) ParseNostrOfferUri(string uri)
        {
            // Format: nostr://offer?uuid=XXXX&relays=wss://a,b
            if (!uri.StartsWith("nostr://offer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid nostr offer URI");
            var qIndex = uri.IndexOf('?');
            if (qIndex < 0 || qIndex + 1 >= uri.Length) throw new InvalidOperationException("Missing query");
            var query = uri.Substring(qIndex + 1);
            var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            string? uuid = null; string? relaysRaw = null;
            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) continue;
                var key = Uri.UnescapeDataString(kv[0]);
                var val = Uri.UnescapeDataString(kv[1]);
                if (key.Equals("uuid", StringComparison.OrdinalIgnoreCase)) uuid = val;
                else if (key.Equals("relays", StringComparison.OrdinalIgnoreCase)) relaysRaw = val;
            }
            if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException("nostr offer missing uuid");
            var relays = string.IsNullOrWhiteSpace(relaysRaw)
                ? Array.Empty<string>()
                : relaysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (uuid!, relays);
        }
    }
}