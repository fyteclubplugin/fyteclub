using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FyteClub.Core.Logging
{
    public enum LogLevel
    {
        Always,    // Critical events that should always be logged
        Debug      // Detailed debug information
    }

    public enum LogModule
    {
        Core,
        UI,
        WebRTC,
        Nostr,
        Cache,
        ModSync,
        Syncshells,
        TURN,
        Penumbra,
        Glamourer,
        CustomizePlus,
        Heels,
        Honorific
    }

    public class LoggingConfig
    {
        public Dictionary<LogModule, bool> EnabledModules { get; set; } = new();
        public bool EnableDebugLogs { get; set; } = false;
    }

    public static class LoggingManager
    {
        private static LoggingConfig _config = new();
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "FyteClub", "logging.json"
        );

        static LoggingManager()
        {
            LoadConfig();
        }

        public static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    _config = JsonSerializer.Deserialize<LoggingConfig>(json) ?? new LoggingConfig();
                }
                else
                {
                    // Default: only enable Core logs
                    _config.EnabledModules[LogModule.Core] = true;
                }
            }
            catch
            {
                _config = new LoggingConfig();
                _config.EnabledModules[LogModule.Core] = true;
            }
        }

        public static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        public static bool ShouldLog(LogLevel level, LogModule module)
        {
            if (level == LogLevel.Always) return true;
            if (!_config.EnableDebugLogs) return false;
            return _config.EnabledModules.GetValueOrDefault(module, false);
        }

        public static void SetModuleEnabled(LogModule module, bool enabled)
        {
            _config.EnabledModules[module] = enabled;
            SaveConfig();
        }

        public static void SetDebugEnabled(bool enabled)
        {
            _config.EnableDebugLogs = enabled;
            SaveConfig();
        }

        public static bool IsModuleEnabled(LogModule module) => _config.EnabledModules.GetValueOrDefault(module, false);
        public static bool IsDebugEnabled() => _config.EnableDebugLogs;
        public static Dictionary<LogModule, bool> GetAllModules() => new(_config.EnabledModules);
    }

    public static class ModularLogger
    {
        public static void Log(LogLevel level, LogModule module, string message, params object[] args)
        {
            if (!LoggingManager.ShouldLog(level, module)) return;
            
            var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            var prefix = level == LogLevel.Always ? "[FYTECLUB]" : $"[{module}]";
            
            SecureLogger.LogInfo("{0} {1}", prefix, formattedMessage);
        }

        public static void LogAlways(LogModule module, string message, params object[] args)
            => Log(LogLevel.Always, module, message, args);

        public static void LogDebug(LogModule module, string message, params object[] args)
            => Log(LogLevel.Debug, module, message, args);
    }
}