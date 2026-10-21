using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlpNetPulse
{
    public class DiscordRpcManager : IDisposable
    {
        private const string CLIENT_ID = "139000000000000000"; // Generic AlpNet Pulse Discord App ID
        private NamedPipeClientStream? _pipeStream;
        private CancellationTokenSource? _cts;
        private bool _isConnected = false;

        private string _currentState = "🚀 Akıllı Bypass Aktif";
        private string _currentDetails = "🛡️ AlpNet Pulse v2.2.8";

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ConnectionLoopAsync(_cts.Token));
        }

        public void UpdatePresence(string state, string details)
        {
            _currentState = state;
            _currentDetails = details;

            if (_isConnected)
            {
                Task.Run(() => SendPresenceAsync());
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
                            string pipeName = $"discord-ipc-{i}";
                            try
                            {
                                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                                await pipe.ConnectAsync(1000, token);
                                _pipeStream = pipe;
                                _isConnected = true;

                                // Send Handshake (Opcode 0)
                                string handshakeJson = JsonSerializer.Serialize(new { v = 1, client_id = CLIENT_ID });
                                await WriteFrameAsync(0, handshakeJson);
                                
                                // Send initial presence
                                await SendPresenceAsync();
                                break;
                            }
                            catch
                            {
                                _pipeStream?.Dispose();
                                _pipeStream = null;
                            }
                        }
                    }
                    catch { }
                }

                await Task.Delay(15000, token).ConfigureAwait(false);
            }
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
                _pipeStream?.Dispose();
                _pipeStream = null;
            }
        }

        private async Task WriteFrameAsync(int opcode, string json)
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

        public void Dispose()
        {
            _cts?.Cancel();
            _pipeStream?.Dispose();
        }
    }
}
