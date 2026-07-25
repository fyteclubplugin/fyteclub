using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FyteClub.Core;
using FyteClub.Core.Logging;

namespace FyteClub.Phonebook
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
                
                FyteLog.Info(LogModule.Syncshells, "Phonebook saved for syncshell with {0} members", phonebook.Members.Count);
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to save phonebook: {0}", ex.Message);
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
                
                // Phonebook entries persist until explicitly deleted by inviters/owners
                // No automatic expiration - deletion requires signed authorization
                
                return phonebook;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to load phonebook: {0}", ex.Message);
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
                    FyteLog.Info(LogModule.Syncshells, "Phonebook deleted for syncshell");
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to delete phonebook: {0}", ex.Message);
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
                        FyteLog.Info(LogModule.Syncshells, "Deleted expired phonebook: {0}", Path.GetFileName(file));
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to cleanup expired phonebooks: {0}", ex.Message);
            }
        }
    }
}