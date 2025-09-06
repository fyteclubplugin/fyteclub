using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class PhonebookPersistence
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _storageDir;

        public PhonebookPersistence(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FyteClub", "phonebooks");
            Directory.CreateDirectory(_storageDir);
        }

        public void SavePhonebook(string syncshellId, SignedPhonebook phonebook)
        {
            try
            {
                var filePath = Path.Combine(_storageDir, $"{syncshellId}.phonebook");
                var json = JsonSerializer.Serialize(phonebook, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                
                _pluginLog.Debug($"Phonebook saved for syncshell {syncshellId} with {phonebook.Members.Count} members");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to save phonebook: {ex.Message}");
            }
        }

        public SignedPhonebook? LoadPhonebook(string syncshellId)
        {
            try
            {
                var filePath = Path.Combine(_storageDir, $"{syncshellId}.phonebook");
                if (!File.Exists(filePath)) return null;

                var json = File.ReadAllText(filePath);
                var phonebook = JsonSerializer.Deserialize<SignedPhonebook>(json);
                
                if (phonebook != null)
                {
                    // Clean up expired entries (24 hour TTL)
                    var cutoff = DateTime.UtcNow.AddHours(-24);
                    var expiredMembers = phonebook.Members.Where(kvp => kvp.Value.LastSeen < cutoff).ToList();
                    
                    foreach (var expired in expiredMembers)
                    {
                        phonebook.Members.Remove(expired.Key);
                    }
                    
                    if (expiredMembers.Count > 0)
                    {
                        _pluginLog.Debug($"Cleaned up {expiredMembers.Count} expired phonebook entries");
                        SavePhonebook(syncshellId, phonebook); // Save cleaned phonebook
                    }
                }
                
                return phonebook;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to load phonebook: {ex.Message}");
                return null;
            }
        }

        public void DeletePhonebook(string syncshellId)
        {
            try
            {
                var filePath = Path.Combine(_storageDir, $"{syncshellId}.phonebook");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _pluginLog.Debug($"Phonebook deleted for syncshell {syncshellId}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to delete phonebook: {ex.Message}");
            }
        }

        public void CleanupExpiredPhonebooks()
        {
            try
            {
                var files = Directory.GetFiles(_storageDir, "*.phonebook");
                var cutoff = DateTimeOffset.UtcNow.AddDays(-7); // Delete phonebooks older than 7 days
                
                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < cutoff)
                    {
                        File.Delete(file);
                        _pluginLog.Debug($"Deleted expired phonebook: {Path.GetFileName(file)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to cleanup expired phonebooks: {ex.Message}");
            }
        }
    }
}