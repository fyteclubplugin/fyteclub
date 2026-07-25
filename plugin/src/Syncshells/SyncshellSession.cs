using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using FyteClub.Core.Logging;

namespace FyteClub.Syncshells
{
    public class SyncshellSession : IDisposable
    {
        public SyncshellIdentity Identity { get; }
        public SyncshellPhonebook Phonebook { get; private set; }
        public bool IsHost { get; private set; }
        public long UptimeCounter { get; private set; }
        
        private TcpListener? _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _disposed;

        public SyncshellSession(SyncshellIdentity identity, SyncshellPhonebook? phonebook, bool isHost)
        {
            Identity = identity;
            Phonebook = phonebook ?? new SyncshellPhonebook();
            IsHost = isHost;
        }

        public Task StartListening()
        {
            if (!IsHost) return Task.CompletedTask;

            _listener = new TcpListener(IPAddress.Any, 7777);
            _listener.Start();
            
            FyteLog.Debug(LogModule.Syncshells, $"Syncshell host listening on port 7777");
            
            _ = FyteClub.Core.SafeTask.Run(AcceptConnections, _cancellation.Token, LogModule.Syncshells);
            return Task.CompletedTask;
        }

        public async Task ConnectToHost(IPAddress hostIP, int hostPort)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(hostIP, hostPort);
            
            // Send authentication
            var authData = System.Text.Encoding.UTF8.GetBytes($"{Identity.Name}:{Convert.ToBase64String(Identity.MasterPasswordHash)}");
            await client.GetStream().WriteAsync(authData, _cancellation.Token);
            
            // Receive phonebook
            var buffer = new byte[65536];
            var received = await client.GetStream().ReadAsync(buffer, _cancellation.Token);
            var phonebookData = new byte[received];
            Array.Copy(buffer, phonebookData, received);
            
            Phonebook = SyncshellPhonebook.Deserialize(phonebookData);
            FyteLog.Debug(LogModule.Syncshells, $"Received phonebook with {Phonebook.Members.Count} members");
        }

        public void IncrementUptime()
        {
            UptimeCounter++;
            
            // Update our entry in phonebook
            var keyStr = Convert.ToBase64String(Identity.PublicKey);
            if (Phonebook.Members.TryGetValue(keyStr, out var member))
            {
                member.UptimeCounter = UptimeCounter;
                member.LastSeen = DateTime.UtcNow;
            }
        }

        public void BecomeHost()
        {
            if (IsHost) return;
            
            IsHost = true;
            FyteLog.Debug(LogModule.Syncshells, "Became new syncshell host");
            _ = FyteClub.Core.SafeTask.Run(() => StartListening(), LogModule.Syncshells);
        }

        private async Task AcceptConnections()
        {
            try
            {
                while (!_cancellation.Token.IsCancellationRequested && _listener != null)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = FyteClub.Core.SafeTask.Run(() => HandleNewMember(client), _cancellation.Token, LogModule.Syncshells);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.Syncshells, $"Error accepting connection: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
        }

        private async Task HandleNewMember(TcpClient client)
        {
            try
            {
                // Read authentication
                var buffer = new byte[1024];
                var received = await client.GetStream().ReadAsync(buffer, _cancellation.Token);
                var authStr = System.Text.Encoding.UTF8.GetString(buffer, 0, received);
                var parts = authStr.Split(':');
                
                if (parts.Length != 2 || parts[0] != Identity.Name)
                {
                    client.Close();
                    return;
                }

                var providedHash = Convert.FromBase64String(parts[1]);
                if (!Identity.MasterPasswordHash.AsSpan().SequenceEqual(providedHash))
                {
                    client.Close();
                    return;
                }

                // Send phonebook
                var phonebookData = Phonebook.Serialize();
                await client.GetStream().WriteAsync(phonebookData, _cancellation.Token);
                
                FyteLog.Debug(LogModule.Syncshells, "New member authenticated and received phonebook");
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, $"Error handling new member: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                _cancellation.Cancel();
                _listener?.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            finally
            {
                _cancellation.Dispose();
                _disposed = true;
            }
        }
    }
}