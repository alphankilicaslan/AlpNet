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
        private string _currentDetails = "🛡️ AlpNet Pulse v2.3.0";

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

        private async Task ConnectionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!_isConnected)
                {
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
                                AlpNetTelemetryService.Instance.Info("DISCORD", $"Discord RPC bağlantısı kuruldu ({pipeName}).");

                                // Send Handshake (Opcode 0)
                                string handshakeJson = JsonSerializer.Serialize(new { v = 1, client_id = CLIENT_ID });
                                await WriteFrameAsync(0, handshakeJson);

                                // Start background frame reader loop to drain incoming frames and avoid pipe buffer overflow
                                _ = Task.Run(() => ReadLoopAsync(pipe, token), token);

                                // Send initial presence
                                await SendPresenceAsync();
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
                }

                try
                {
                    await Task.Delay(15000, token).ConfigureAwait(false);
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
