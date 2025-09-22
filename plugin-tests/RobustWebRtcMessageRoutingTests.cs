using System;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FyteClub.WebRTC;
using Xunit;

namespace FyteClub.Tests
{
    public class RobustWebRtcMessageRoutingTests
    {
        private static async Task InvokeHandleP2PMessageAsync(RobustWebRTCConnection conn, string json)
        {
            var method = typeof(RobustWebRTCConnection).GetMethod(
                "HandleP2PMessage",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.NotNull(method);
            var task = (Task?)method!.Invoke(conn, new object[] { Encoding.UTF8.GetBytes(json) });
            Assert.NotNull(task);
            await task!;
        }

        [Fact]
        public async Task Ignores_NonObject_Message()
        {
            var conn = new RobustWebRTCConnection();
            string payload = "\"not-an-object\""; // valid JSON string, but not an object

            string? receivedPlayer = null;
            conn.OnModDataReceived += (player, _) => receivedPlayer = player;

            await InvokeHandleP2PMessageAsync(conn, payload);

            Assert.Null(receivedPlayer);
        }

        [Fact]
        public async Task Ignores_Missing_Type_Field()
        {
            var conn = new RobustWebRTCConnection();
            string payload = "{\"foo\":123}";

            string? receivedPlayer = null;
            conn.OnModDataReceived += (player, _) => receivedPlayer = player;

            await InvokeHandleP2PMessageAsync(conn, payload);

            Assert.Null(receivedPlayer);
        }

        [Fact]
        public async Task Forwards_Valid_ModData_To_Event()
        {
            var conn = new RobustWebRTCConnection();
            string payload = JsonSerializer.Serialize(new {
                type = "mod_data",
                playerName = "Alice",
                mods = new[] { "h1", "h2" },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            string? receivedPlayer = null;
            JsonElement receivedMessage = default;
            conn.OnModDataReceived += (player, msg) => { receivedPlayer = player; receivedMessage = msg; };

            await InvokeHandleP2PMessageAsync(conn, payload);

            Assert.Equal("Alice", receivedPlayer);
            Assert.True(receivedMessage.TryGetProperty("type", out var typeProp));
            Assert.Equal("mod_data", typeProp.GetString());
        }

        [Fact]
        public async Task Rejects_ModData_Without_PlayerName()
        {
            var conn = new RobustWebRTCConnection();
            string payload = JsonSerializer.Serialize(new {
                type = "mod_data",
                mods = Array.Empty<string>()
            });

            string? receivedPlayer = null;
            conn.OnModDataReceived += (player, _) => receivedPlayer = player;

            await InvokeHandleP2PMessageAsync(conn, payload);

            Assert.Null(receivedPlayer);
        }

        [Fact]
        public async Task Rejects_ModData_With_Empty_PlayerName()
        {
            var conn = new RobustWebRTCConnection();
            string payload = JsonSerializer.Serialize(new {
                type = "mod_data",
                playerName = "",
                mods = Array.Empty<string>()
            });

            string? receivedPlayer = null;
            conn.OnModDataReceived += (player, _) => receivedPlayer = player;

            await InvokeHandleP2PMessageAsync(conn, payload);

            Assert.Null(receivedPlayer);
        }

        [Fact]
        public async Task Unknown_Type_Does_Not_Touch_ModData_Handler()
        {
            var conn = new RobustWebRTCConnection();
            string payload = JsonSerializer.Serialize(new { type = "unknown_thing", foo = 1 });

            string? receivedPlayer = null;
            conn.OnModDataReceived += (player, _) => receivedPlayer = player;

            await InvokeHandleP2PMessageAsync(conn, payload);

            Assert.Null(receivedPlayer);
        }
    }
}