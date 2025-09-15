using System;
using System.IO;
using System.Threading.Tasks;

namespace FyteClub.Plugin
{
    public class FileSignaling
    {
        private readonly string syncshellId;
        private readonly string tempPath;
        
        public FileSignaling(string syncshellId)
        {
            this.syncshellId = syncshellId;
            this.tempPath = Path.Combine(Path.GetTempPath(), "fyteclub", syncshellId);
            Directory.CreateDirectory(tempPath);
        }
        
        public async Task WriteOffer(string offer)
        {
            var offerFile = Path.Combine(tempPath, "offer.txt");
            await File.WriteAllTextAsync(offerFile, offer);
        }
        
        public async Task WriteAnswer(string answer)
        {
            var answerFile = Path.Combine(tempPath, "answer.txt");
            await File.WriteAllTextAsync(answerFile, answer);
        }
        
        public async Task<string?> ReadAnswer()
        {
            var answerFile = Path.Combine(tempPath, "answer.txt");
            if (File.Exists(answerFile))
            {
                return await File.ReadAllTextAsync(answerFile);
            }
            return null;
        }
        
        public async Task<string?> ReadOffer()
        {
            var offerFile = Path.Combine(tempPath, "offer.txt");
            if (File.Exists(offerFile))
            {
                return await File.ReadAllTextAsync(offerFile);
            }
            return null;
        }
    }
}