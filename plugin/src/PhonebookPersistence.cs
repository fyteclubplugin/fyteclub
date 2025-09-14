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
                if (!InputValidator.IsValidSyncshellId(syncshellId))
                    throw new ArgumentException("Invalid syncshell ID");
                    
                var sanitizedId = Path.GetFileName(syncshellId); // Prevent path traversal
                var filePath = Path.Combine(_storageDir, $"{sanitizedId}.phonebook");
                
                // Validate the final path is within storage directory
                var fullPath = Path.GetFullPath(filePath);
                var fullStorageDir = Path.GetFullPath(_storageDir);
                if (!fullPath.StartsWith(fullStorageDir))
                    throw new UnauthorizedAccessException("Path traversal attempt detected");
                    
                var json = JsonSerializer.Serialize(phonebook, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                
                SecureLogger.LogInfo("Phonebook saved for syncshell with {0} members", phonebook.Members.Count);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to save phonebook: {0}", ex.Message);
            }
        }

        public SignedPhonebook? LoadPhonebook(string syncshellId)
        {
            try
            {
                if (!InputValidator.IsValidSyncshellId(syncshellId))
                    throw new ArgumentException("Invalid syncshell ID");
                    
                var sanitizedId = Path.GetFileName(syncshellId); // Prevent path traversal
                var filePath = Path.Combine(_storageDir, $"{sanitizedId}.phonebook");
                
                // Validate the final path is within storage directory
                var fullPath = Path.GetFullPath(filePath);
                var fullStorageDir = Path.GetFullPath(_storageDir);
                if (!fullPath.StartsWith(fullStorageDir))
                    throw new UnauthorizedAccessException("Path traversal attempt detected");
                    
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
                        SecureLogger.LogInfo("Cleaned up {0} expired phonebook entries", expiredMembers.Count);
                        SavePhonebook(syncshellId, phonebook); // Save cleaned phonebook
                    }
                }
                
                return phonebook;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to load phonebook: {0}", ex.Message);
                return null;
            }
        }

        public void DeletePhonebook(string syncshellId)
        {
            try
            {
                if (!InputValidator.IsValidSyncshellId(syncshellId))
                    throw new ArgumentException("Invalid syncshell ID");
                    
                var sanitizedId = Path.GetFileName(syncshellId); // Prevent path traversal
                var filePath = Path.Combine(_storageDir, $"{sanitizedId}.phonebook");
                
                // Validate the final path is within storage directory
                var fullPath = Path.GetFullPath(filePath);
                var fullStorageDir = Path.GetFullPath(_storageDir);
                if (!fullPath.StartsWith(fullStorageDir))
                    throw new UnauthorizedAccessException("Path traversal attempt detected");
                    
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    SecureLogger.LogInfo("Phonebook deleted for syncshell");
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to delete phonebook: {0}", ex.Message);
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
                        SecureLogger.LogInfo("Deleted expired phonebook: {0}", Path.GetFileName(file));
                    }
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to cleanup expired phonebooks: {0}", ex.Message);
            }
        }
    }
}