using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FyteClub
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

        public async Task StartListening()
        {
            if (!IsHost) return;

            _listener = new TcpListener(IPAddress.Any, 7777);
            _listener.Start();
            
            Console.WriteLine($"Syncshell host listening on port 7777");
            
            _ = Task.Run(AcceptConnections, _cancellation.Token);
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
            Console.WriteLine($"Received phonebook with {Phonebook.Members.Count} members");
        }

        public string GenerateInviteCode()
        {
            if (!IsHost) throw new InvalidOperationException("Only host can generate invite codes");
            
            var localIP = GetLocalIPAddress();
            return InviteCodeGenerator.GenerateCode(localIP, 7777, Identity.EncryptionKey, Phonebook.SequenceCounter, Identity.Ed25519Identity.ExportPrivateKey());
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
            Console.WriteLine("Became new syncshell host");
            _ = Task.Run(() => StartListening());
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
                        _ = Task.Run(() => HandleNewMember(client), _cancellation.Token);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error accepting connection: {ex.Message}");
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
                
                Console.WriteLine("New member authenticated and received phonebook");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling new member: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }

        private static IPAddress GetLocalIPAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)socket.LocalEndPoint!).Address;
            }
            catch
            {
                return IPAddress.Loopback;
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