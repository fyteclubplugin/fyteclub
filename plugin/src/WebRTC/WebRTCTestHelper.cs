using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public static class WebRTCTestHelper
    {
        public static async Task<bool> TestMinimalConnection(IPluginLog pluginLog)
        {
            pluginLog.Info("[WebRTC Test] Starting minimal connection test");
            
            try
            {
                // Create host connection
                var hostConnection = new RobustWebRTCConnection(pluginLog);
                var clientConnection = new RobustWebRTCConnection(pluginLog);
                
                var hostConnected = false;
                var clientConnected = false;
                var testMessageReceived = false;
                
                hostConnection.OnConnected += () => {
                    pluginLog.Info("[WebRTC Test] Host connected");
                    hostConnected = true;
                };
                
                clientConnection.OnConnected += () => {
                    pluginLog.Info("[WebRTC Test] Client connected");
                    clientConnected = true;
                };
                
                clientConnection.OnDataReceived += (data) => {
                    var message = System.Text.Encoding.UTF8.GetString(data);
                    pluginLog.Info($"[WebRTC Test] Client received: {message}");
                    if (message.Contains("test"))
                        testMessageReceived = true;
                };
                
                // Initialize connections
                await hostConnection.InitializeAsync();
                await clientConnection.InitializeAsync();
                
                // Create offer
                var offer = await hostConnection.CreateOfferAsync();
                pluginLog.Info($"[WebRTC Test] Offer created: {offer.Length} chars");
                
                // Generate invite with ICE
                var inviteCode = hostConnection.GenerateInviteWithIce("test", "password", offer);
                pluginLog.Info($"[WebRTC Test] Invite code: {inviteCode.Length} chars");
                
                // Process invite on client
                clientConnection.ProcessInviteWithIce(inviteCode);
                
                // Wait for connection
                await Task.Delay(5000);
                
                // Test data sending if connected
                if (hostConnected && clientConnected)
                {
                    var testData = System.Text.Encoding.UTF8.GetBytes("test message");
                    await hostConnection.SendDataAsync(testData);
                    
                    await Task.Delay(1000);
                }
                
                var success = hostConnected && clientConnected && testMessageReceived;
                pluginLog.Info($"[WebRTC Test] Result - Host: {hostConnected}, Client: {clientConnected}, Message: {testMessageReceived}, Success: {success}");
                
                hostConnection.Dispose();
                clientConnection.Dispose();
                
                return success;
            }
            catch (Exception ex)
            {
                pluginLog.Error($"[WebRTC Test] Failed: {ex.Message}");
                return false;
            }
        }
    }
}