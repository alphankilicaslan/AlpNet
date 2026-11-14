using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlpNetPulse
{
    public class DiscordRpcManager : IDisposable
    {
        private const string CLIENT_ID = "139000000000000000";
        private NamedPipeClientStream? _pipeStream;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private volatile bool _isConnected = false;

        private string _currentState = "🚀 Akıllı Bypass Aktif";
        private string _currentDetails = "🛡️ AlpNet Pulse v2.3.5";

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        }

        public void UpdatePresence(string state, string details)
        {
            _currentState = state;
            _currentDetails = details;

            if (_isConnected)
            {
                _ = Task.Run(SendPresenceAsync);
            }
        }

        private static bool IsConfiguredValidClientId(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId)) return false;
            if (clientId == "139000000000000000") return false;
            if (clientId.Length > 10 && clientId.EndsWith("0000000000")) return false;
            return true;
        }

        private async Task ConnectionLoopAsync(CancellationToken token)
        {
            if (!IsConfiguredValidClientId(CLIENT_ID))
            {
                AlpNetTelemetryService.Instance.Log("DISCORD", "Discord RPC: Uygulama ID yapılandırılmamış (varsayılan dummy ID). IPC döngüsü başlatılmadı.");
                return;
            }

            int failedAttempts = 0;
            int failedHandshakes = 0;

            while (!token.IsCancellationRequested)
            {
                if (!_isConnected)
                {
                    bool connectedThisCycle = false;
                    try
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            if (token.IsCancellationRequested) break;
                            string pipeName = $"discord-ipc-{i}";
                            NamedPipeClientStream? pipe = null;
                            try
                            {
                                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                                connectCts.CancelAfter(800);
                                await pipe.ConnectAsync(connectCts.Token);

                                _pipeStream = pipe;
                                _isConnected = true;
                                connectedThisCycle = true;
                                AlpNetTelemetryService.Instance.Info("DISCORD", $"Discord RPC bağlantısı kuruldu ({pipeName}).");

                                // Send Handshake (Opcode 0)
                                string handshakeJson = JsonSerializer.Serialize(new { v = 1, client_id = CLIENT_ID });
                                await WriteFrameAsync(0, handshakeJson);

                                // Start background frame reader loop to drain incoming frames and avoid pipe buffer overflow
                                _ = Task.Run(() => ReadLoopAsync(pipe, token), token);

                                // Send initial presence
                                await SendPresenceAsync();
                                failedAttempts = 0;
                                failedHandshakes = 0;
                                break;
                            }
                            catch
                            {
                                pipe?.Dispose();
                                _pipeStream = null;
                                _isConnected = false;
                            }
                        }
                    }
                    catch { }

                    if (!connectedThisCycle)
                    {
                        failedAttempts++;
                    }
                }

                if (failedHandshakes >= 3)
                {
                    AlpNetTelemetryService.Instance.Warn("DISCORD", "Discord RPC: 3 başarısız el sıkışma denemesinden sonra IPC döngüsü sonlandırıldı.");
                    break;
                }

                try
                {
                    // Exponential backoff: 15s -> 30s -> 60s -> max 120s
                    int delayMs = Math.Min(15000 * (int)Math.Pow(2, Math.Max(0, failedAttempts - 1)), 120000);
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task ReadLoopAsync(NamedPipeClientStream stream, CancellationToken token)
        {
            byte[] header = new byte[8];
            try
            {
                while (!token.IsCancellationRequested && stream.IsConnected)
                {
                    int read = await ReadExactAsync(stream, header, 0, 8, token);
                    if (read < 8) break;

                    int opcode = BitConverter.ToInt32(header, 0);
                    int length = BitConverter.ToInt32(header, 4);

                    if (length > 0 && length < 65536)
                    {
                        byte[] payload = new byte[length];
                        int payloadRead = await ReadExactAsync(stream, payload, 0, length, token);
                        if (payloadRead < length) break;
                    }
                }
            }
            catch { }
            finally
            {
                _isConnected = false;
                try { stream.Dispose(); } catch { }
                if (_pipeStream == stream) _pipeStream = null;
            }
        }

        private static async Task<int> ReadExactAsync(NamedPipeClientStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, offset + total, count - total, token);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private async Task SendPresenceAsync()
        {
            if (_pipeStream == null || !_isConnected) return;

            try
            {
                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Process.GetCurrentProcess().Id,
                        activity = new
                        {
                            state = _currentState,
                            details = _currentDetails,
                            assets = new
                            {
                                large_image = "alpnet_logo",
                                large_text = "AlpNet Pulse Gaming Suite"
                            }
                        }
                    },
                    nonce = Guid.NewGuid().ToString("N")
                };

                string json = JsonSerializer.Serialize(payload);
                await WriteFrameAsync(1, json);
            }
            catch
            {
                _isConnected = false;
                try { _pipeStream?.Dispose(); } catch { }
                _pipeStream = null;
            }
        }

        private async Task WriteFrameAsync(int opcode, string json)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_pipeStream == null || !_pipeStream.IsConnected) return;

                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                byte[] opcodeBytes = BitConverter.GetBytes(opcode);
                byte[] lengthBytes = BitConverter.GetBytes(jsonBytes.Length);

                byte[] buffer = new byte[8 + jsonBytes.Length];
                Array.Copy(opcodeBytes, 0, buffer, 0, 4);
                Array.Copy(lengthBytes, 0, buffer, 4, 4);
                Array.Copy(jsonBytes, 0, buffer, 8, jsonBytes.Length);

                await _pipeStream.WriteAsync(buffer, 0, buffer.Length);
                await _pipeStream.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _isConnected = false;
            try { _pipeStream?.Dispose(); } catch { }
            _pipeStream = null;
            _writeLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
