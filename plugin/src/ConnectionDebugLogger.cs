using System;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public static class ConnectionDebugLogger
    {
        private static IPluginLog? _log;
        
        public static void Initialize(IPluginLog log)
        {
            _log = log;
        }
        
        public static void LogConnectionAttempt(string syncshellId, string role, string step)
        {
            _log?.Info($"[P2P-DEBUG] {role} - {step} - Syncshell: {syncshellId}");
            Console.WriteLine($"[P2P-DEBUG] {role} - {step} - Syncshell: {syncshellId}");
        }
        
        public static void LogWebRTCState(string connectionId, string state, string details = "")
        {
            _log?.Info($"[WebRTC-DEBUG] {connectionId} - {state} - {details}");
            Console.WriteLine($"[WebRTC-DEBUG] {connectionId} - {state} - {details}");
        }
        
        public static void LogDataExchange(string from, string to, string dataType, int bytes)
        {
            _log?.Info($"[DATA-DEBUG] {from} -> {to} - {dataType} - {bytes} bytes");
            Console.WriteLine($"[DATA-DEBUG] {from} -> {to} - {dataType} - {bytes} bytes");
        }
    }
}