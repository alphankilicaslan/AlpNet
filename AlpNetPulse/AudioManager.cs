using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using System.Collections.Generic;

namespace AlpNetPulse
{
    public class AudioManager : IDisposable
    {
        private WasapiLoopbackCapture? _loopbackCapture;
        private WaveInEvent? _micCapture;
        
        private BufferedWaveProvider? _sysProvider;
        private BufferedWaveProvider? _micProvider;
        
        private ISampleProvider? _sysSampleProvider;
        private ISampleProvider? _micSampleProvider;

        private WaveFileWriter? _sysWriter;
        private WaveFileWriter? _micWriter;
        private Thread? _writerThread;
        
        private string _segmentDir = "";
        private int _segmentDurationSeconds = 10;
        private int _segmentIndex = 0;
        private bool _isRecording = false;
        private DateTime _currentSegmentStartTime;
        private long _sysWrittenBytes = 0;
        private long _micWrittenBytes = 0;
        private readonly object _writerLock = new object();
        private WaveFormat _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int CurrentSegmentIndex
        {
            get
            {
                lock (_writerLock)
                {
                    return _segmentIndex;
                }
            }
        }

        public void StartCapture(string segmentDir, int segmentDurationSeconds, bool recordSystem, bool recordMic, string micDeviceName, int initialSegmentIndex = 0)
        {
            if (_isRecording) return;
            
            _segmentDir = segmentDir;
            _segmentDurationSeconds = segmentDurationSeconds;
            _segmentIndex = initialSegmentIndex;
            _isRecording = true;

            try
            {
                if (recordSystem)
                {
                    _loopbackCapture = new WasapiLoopbackCapture();
                    _sysProvider = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(5),
                        ReadFully = true
                    };
                    
                    var loopbackResampler = new WdlResamplingSampleProvider(_sysProvider.ToSampleProvider(), 48000);
                    _sysSampleProvider = new SurroundToStereoProvider(loopbackResampler);

                    _loopbackCapture.DataAvailable += (s, a) =>
                    {
                        try
                        {
                            if (_isRecording && _sysProvider != null)
                                _sysProvider.AddSamples(a.Buffer, 0, a.BytesRecorded);
                        }
                        catch { }
                    };
                    _loopbackCapture.StartRecording();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("System Audio Capture Error: " + ex.Message); }

            try
            {
                if (recordMic)
                {
                    int micIndex = -1;
                    if (!string.IsNullOrEmpty(micDeviceName) && micDeviceName != "Varsayılan Mikrofon")
                    {
                        for (int i = 0; i < WaveIn.DeviceCount; i++)
                        {
                            try
                            {
                                string prodName = WaveIn.GetCapabilities(i).ProductName;
                                if (micDeviceName.StartsWith(prodName) || prodName.StartsWith(micDeviceName))
                                {
                                    micIndex = i;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    _micCapture = new WaveInEvent();
                    if (micIndex >= 0) _micCapture.DeviceNumber = micIndex;
                    
                    // Mikrofon sürücü uyumluluğu için 48000Hz -> 44100Hz -> Varsayılan biçim sırasıyla denenir
                    try
                    {
                        _micCapture.WaveFormat = new WaveFormat(48000, 1);
                    }
                    catch
                    {
                        try { _micCapture.WaveFormat = new WaveFormat(44100, 1); }
                        catch { }
                    }

                    _micProvider = new BufferedWaveProvider(_micCapture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(5),
                        ReadFully = true
                    };
                    
                    var micResampler = new WdlResamplingSampleProvider(_micProvider.ToSampleProvider(), 48000);
                    _micSampleProvider = new SurroundToStereoProvider(micResampler);

                    _micCapture.DataAvailable += (s, a) =>
                    {
                        try
                        {
                            if (_isRecording && _micProvider != null)
                                _micProvider.AddSamples(a.Buffer, 0, a.BytesRecorded);
                        }
                        catch { }
                    };
                    _micCapture.StartRecording();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Mic Audio Capture Error: " + ex.Message); }

            if (recordSystem || recordMic)
            {
                _writerThread = new Thread(WriterLoop) { IsBackground = true };
                _writerThread.Start();
            }
        }

        private void WriterLoop()
        {
            float[] sysBuffer = new float[4800 * 2]; // 100ms buffer chunk
            float[] micBuffer = new float[4800 * 2];
            
            StartNewSegment();

            while (_isRecording)
            {
                int sysRead = 0, micRead = 0;
                try
                {
                    if (_sysSampleProvider != null) sysRead = _sysSampleProvider.Read(sysBuffer, 0, sysBuffer.Length);
                    if (_micSampleProvider != null) micRead = _micSampleProvider.Read(micBuffer, 0, micBuffer.Length);
                }
                catch { }

                lock (_writerLock)
                {
                    if (_sysWriter != null)
                    {
                        if (sysRead > 0)
                        {
                            _sysWriter.WriteSamples(sysBuffer, 0, sysRead);
                            _sysWrittenBytes += sysRead * 4;
                        }
                        FillSilence(_sysWriter, ref _sysWrittenBytes);
                    }
                    if (_micWriter != null)
                    {
                        if (micRead > 0)
                        {
                            _micWriter.WriteSamples(micBuffer, 0, micRead);
                            _micWrittenBytes += micRead * 4;
                        }
                        FillSilence(_micWriter, ref _micWrittenBytes);
                    }
                }

                if (sysRead == 0 && micRead == 0)
                {
                    Thread.Sleep(50);
                }

                if ((DateTime.Now - _currentSegmentStartTime).TotalSeconds >= _segmentDurationSeconds)
                {
                    StartNewSegment();
                }
            }

            lock (_writerLock)
            {
                try { _sysWriter?.Dispose(); } catch { }
                _sysWriter = null;
                try { _micWriter?.Dispose(); } catch { }
                _micWriter = null;
            }
        }

        private void FillSilence(WaveFileWriter writer, ref long writtenBytes)
        {
            try
            {
                long expectedBytes = (long)((DateTime.Now - _currentSegmentStartTime).TotalSeconds * writer.WaveFormat.AverageBytesPerSecond);
                long missingBytes = expectedBytes - writtenBytes;
                
                if (missingBytes >= 4)
                {
                    int bytesToFill = (int)(missingBytes - (missingBytes % writer.WaveFormat.BlockAlign));
                    if (bytesToFill > 0)
                    {
                        byte[] silence = new byte[bytesToFill];
                        writer.Write(silence, 0, bytesToFill);
                        writtenBytes += bytesToFill;
                    }
                }
            }
            catch { }
        }

        private void StartNewSegment()
        {
            lock (_writerLock)
            {
                try { _sysWriter?.Dispose(); } catch { }
                _sysWriter = null;
                try { _micWriter?.Dispose(); } catch { }
                _micWriter = null;

                string sysFileName = Path.Combine(_segmentDir, $"alpnet_seg_sys_{_segmentIndex:D6}.wav");
                string micFileName = Path.Combine(_segmentDir, $"alpnet_seg_mic_{_segmentIndex:D6}.wav");
                
                _segmentIndex++;
                _sysWrittenBytes = 0;
                _micWrittenBytes = 0;
                
                try
                {
                    if (_sysSampleProvider != null) _sysWriter = new WaveFileWriter(sysFileName, _targetFormat);
                    if (_micSampleProvider != null) _micWriter = new WaveFileWriter(micFileName, _targetFormat);
                }
                catch { }
                
                _currentSegmentStartTime = DateTime.Now;
            }
        }

        public void StopCapture()
        {
            if (!_isRecording && _loopbackCapture == null && _micCapture == null) return;
            _isRecording = false;
            
            try { _loopbackCapture?.StopRecording(); } catch { }
            try { _micCapture?.StopRecording(); } catch { }
            
            try { _writerThread?.Join(1000); } catch { }

            lock (_writerLock)
            {
                try { _sysWriter?.Dispose(); } catch { }
                _sysWriter = null;
                try { _micWriter?.Dispose(); } catch { }
                _micWriter = null;
            }

            try { _loopbackCapture?.Dispose(); } catch { }
            try { _micCapture?.Dispose(); } catch { }

            _loopbackCapture = null;
            _micCapture = null;
            _sysProvider = null;
            _micProvider = null;
            _sysSampleProvider = null;
            _micSampleProvider = null;

            // Sürücünün ses işleyicisini (waveIn handle) tamamen serbest bırakması için kısa güvenlik beklemesi
            System.Threading.Thread.Sleep(150);
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}
