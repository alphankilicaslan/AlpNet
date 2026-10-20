using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace AlpNetPulse
{
    /// <summary>
    /// Manages FFmpeg-based screen recording with a circular buffer of segments.
    /// Segments are 10-second chunks; old segments beyond the buffer window are pruned.
    /// When a clip is requested, relevant segments are concatenated into an MP4 file.
    /// </summary>
    public class FFmpegManager : IDisposable
    {
        private const int SEGMENT_DURATION_SECONDS = 2;
        private const string SEGMENT_PATTERN = "alpnet_seg_%06d.mp4";

        private Process? _ffmpegProcess;
        private string _ffmpegPath = "";
        private string _segmentDir = "";
        private string _encoder = "";
        private bool _isRecording = false;
        private bool _disposed = false;
        private System.Threading.Timer? _cleanupTimer;
        private int _bufferMinutes = 10;
        private int _fps = 30;
        private string _bitrate = "8M";
        private bool _recordAudio = true;
        private bool _recordMic = true;
        private string _micDeviceName = "";

        private AudioManager? _audioManager;

        /// <summary>
        /// Raised when a status message should be displayed to the user.
        /// </summary>
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Raised when recording state changes.
        /// </summary>
        public event Action<bool>? RecordingStateChanged;

        /// <summary>
        /// Raised when a clip has been successfully saved.
        /// </summary>
        public event Action<string>? ClipSaved;

        /// <summary>
        /// Raised when an error occurs.
        /// </summary>
        public event Action<string>? ErrorOccurred;

        public bool IsRecording => _isRecording;
        public string DetectedEncoder => _encoder;

        /// <summary>
        /// Gets the current disk usage of segment files in MB.
        /// </summary>
        public double GetSegmentDiskUsageMB()
        {
            try
            {
                if (!Directory.Exists(_segmentDir)) return 0;
                return new DirectoryInfo(_segmentDir)
                    .GetFiles("alpnet_seg_*.mp4")
                    .Sum(f => f.Length) / (1024.0 * 1024.0);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Gets the number of segment files.
        /// </summary>
        public int GetSegmentCount()
        {
            try
            {
                if (!Directory.Exists(_segmentDir)) return 0;
                return Directory.GetFiles(_segmentDir, "alpnet_seg_*.mp4").Length;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Initializes and locates FFmpeg. Returns true if FFmpeg is found.
        /// </summary>
        public async Task<bool> InitializeAsync(string customFfmpegPath = "")
        {
            // Set up segment directory
            _segmentDir = Path.Combine(Path.GetTempPath(), "AlpNetReplay");
            if (!Directory.Exists(_segmentDir))
                Directory.CreateDirectory(_segmentDir);

            // Try to find FFmpeg
            _ffmpegPath = FindFFmpeg(customFfmpegPath);
            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                // Try to download FFmpeg
                StatusChanged?.Invoke("FFmpeg bulunamadı. İndiriliyor...");
                _ffmpegPath = await DownloadFFmpegAsync();
            }

            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                ErrorOccurred?.Invoke("FFmpeg bulunamadı ve indirilemedi. Lütfen FFmpeg'i manuel olarak yükleyin.");
                return false;
            }

            // Detect best encoder
            _encoder = await DetectBestEncoderAsync();
            StatusChanged?.Invoke($"FFmpeg hazır. Encoder: {_encoder}");
            return true;
        }

        /// <summary>
        /// Starts recording with the specified settings.
        /// </summary>
        private bool _useFallbackGdi = false;

        /// <summary>
        /// Starts recording with the specified settings.
        /// </summary>
        public void StartRecording(int bufferMinutes, int fps, string bitrate, bool recordAudio, bool recordMic, string micDeviceName)
        {
            if (_isRecording) return;
            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                ErrorOccurred?.Invoke("FFmpeg bulunamadı. Önce ayarları kontrol edin.");
                return;
            }

            _bufferMinutes = bufferMinutes;
            _fps = fps;
            _bitrate = bitrate;
            _recordAudio = recordAudio;
            _recordMic = recordMic;
            _micDeviceName = micDeviceName;

            // Clean old segments before starting
            CleanAllSegments();

            try
            {
                // Build FFmpeg encoder arguments
                string encoderArgs = _encoder switch
                {
                    "h264_nvenc" => $"-c:v h264_nvenc -preset p4 -tune hq -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate} -g {_fps}",
                    "h264_amf" => $"-c:v h264_amf -quality balanced -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate} -g {_fps}",
                    "h264_qsv" => $"-c:v h264_qsv -preset medium -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate} -g {_fps}",
                    _ => $"-c:v libx264 -preset ultrafast -tune zerolatency -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate} -g {_fps}"
                };

                string segmentPath = Path.Combine(_segmentDir, SEGMENT_PATTERN);

                // Use DXGI DDA (ddagrab) for low-CPU 60fps recording, fallback to gdigrab if DDA fails on GPU
                string videoInput = _useFallbackGdi 
                    ? $"-f gdigrab -framerate {_fps} -i desktop" 
                    : $"-f lavfi -thread_queue_size 512 -i \"ddagrab=framerate={_fps}:draw_mouse=0,hwdownload,format=bgra\"";

                // Video-only segment recording (System audio & mic are captured in parallel by NAudio WASAPI AudioManager)
                string args = $"{videoInput} " +
                              $"{encoderArgs} -pix_fmt yuv420p -an " +
                              $"-f segment -segment_time {SEGMENT_DURATION_SECONDS} " +
                              $"-reset_timestamps 1 -segment_wrap 0 " +
                              $"-y \"{segmentPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                };

                _ffmpegProcess = new Process { StartInfo = psi };
                _ffmpegProcess.Start();

                // Set process to below normal priority to minimize game impact
                try { _ffmpegProcess.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                _isRecording = true;
                RecordingStateChanged?.Invoke(true);
                StatusChanged?.Invoke("Kayıt başladı. Arka planda çalışıyor...");

                if (_recordAudio || _recordMic)
                {
                    _audioManager = new AudioManager();
                    _audioManager.StartCapture(_segmentDir, SEGMENT_DURATION_SECONDS, _recordAudio, _recordMic, _micDeviceName);
                }

                // Start cleanup timer (every 10 seconds)
                _cleanupTimer = new System.Threading.Timer(CleanupTimerCallback, null, 
                    TimeSpan.FromSeconds(SEGMENT_DURATION_SECONDS), 
                    TimeSpan.FromSeconds(SEGMENT_DURATION_SECONDS));

                // Monitor FFmpeg stderr for errors asynchronously
                Task.Run(() => MonitorFFmpegOutput());
            }
            catch (Exception ex)
            {
                _isRecording = false;
                RecordingStateChanged?.Invoke(false);
                ErrorOccurred?.Invoke($"Kayıt başlatılamadı: {ex.Message}");
            }
        }

        /// <summary>
        /// Dynamically updates audio capture settings during active recording.
        /// </summary>
        public void UpdateAudioSettings(bool recordAudio, bool recordMic, string micDeviceName)
        {
            _recordAudio = recordAudio;
            _recordMic = recordMic;
            _micDeviceName = micDeviceName;

            if (_isRecording)
            {
                try
                {
                    int nextSegmentIndex = 0;
                    if (_audioManager != null)
                    {
                        nextSegmentIndex = _audioManager.CurrentSegmentIndex;
                    }

                    if (Directory.Exists(_segmentDir))
                    {
                        int maxDiskIndex = -1;
                        foreach (var f in new DirectoryInfo(_segmentDir).GetFiles("alpnet_seg_*.*"))
                        {
                            string name = Path.GetFileNameWithoutExtension(f.Name);
                            int lastUnderscore = name.LastIndexOf('_');
                            if (lastUnderscore >= 0 && int.TryParse(name.Substring(lastUnderscore + 1), out int parsedIdx))
                            {
                                if (parsedIdx > maxDiskIndex) maxDiskIndex = parsedIdx;
                            }
                        }
                        if (maxDiskIndex >= 0)
                        {
                            nextSegmentIndex = Math.Max(nextSegmentIndex, maxDiskIndex + 1);
                        }
                    }

                    _audioManager?.StopCapture();
                    _audioManager?.Dispose();
                    _audioManager = null;

                    if (_recordAudio || _recordMic)
                    {
                        _audioManager = new AudioManager();
                        _audioManager.StartCapture(_segmentDir, SEGMENT_DURATION_SECONDS, _recordAudio, _recordMic, _micDeviceName, nextSegmentIndex);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("UpdateAudioSettings Error: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Stops the current recording.
        /// </summary>
        public void StopRecording(bool cleanSegments = false)
        {
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;

            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    // Send 'q' to FFmpeg stdin for graceful shutdown
                    _ffmpegProcess.StandardInput.Write("q");
                    _ffmpegProcess.StandardInput.Flush();
                    
                    if (!_ffmpegProcess.WaitForExit(3000))
                    {
                        _ffmpegProcess.Kill();
                    }
                }
                catch
                {
                    try { _ffmpegProcess.Kill(); } catch { }
                }
            }

            _ffmpegProcess = null;

            _audioManager?.StopCapture();
            _audioManager?.Dispose();
            _audioManager = null;

            _isRecording = false;
            RecordingStateChanged?.Invoke(false);
            StatusChanged?.Invoke("Kayıt durduruldu.");

            if (cleanSegments)
            {
                CleanAllSegments();
            }
        }

        /// <summary>
        /// Saves the last N minutes as a clip by concatenating segments.
        /// </summary>
        public async Task<string?> SaveClipAsync(string saveFolder, int? overrideMinutes = null)
        {
            int minutes = overrideMinutes ?? _bufferMinutes;

            try
            {
                if (!Directory.Exists(saveFolder))
                    Directory.CreateDirectory(saveFolder);

                // Get segment files sorted by creation time
                var segmentFiles = new DirectoryInfo(_segmentDir)
                    .GetFiles("alpnet_seg_*.mp4")
                    .OrderBy(f => f.CreationTime)
                    .ToList();

                if (segmentFiles.Count == 0)
                {
                    ErrorOccurred?.Invoke("Kaydedilecek segment bulunamadı. Kayıt çalışıyor mu?");
                    return null;
                }

                // Calculate how many segments we need for the requested duration
                int segmentsNeeded = (minutes * 60) / SEGMENT_DURATION_SECONDS;
                if (_isRecording) segmentsNeeded++; // Ask for 1 more because we will skip the last one
                
                // Take the last N segments
                var clippedSegments = segmentFiles
                    .Skip(Math.Max(0, segmentFiles.Count - segmentsNeeded))
                    .ToList();

                // If recording is active, skip the very last segment (it's still being written to)
                if (_isRecording && clippedSegments.Count > 1)
                {
                    clippedSegments = clippedSegments.Take(clippedSegments.Count - 1).ToList();
                }

                if (clippedSegments.Count == 0)
                {
                    ErrorOccurred?.Invoke("Yeterli kayıt verisi yok. Biraz daha bekleyin.");
                    return null;
                }

                // Create concat list file
                string concatListPath = Path.Combine(_segmentDir, "concat_list.txt");
                var lines = clippedSegments.Select(f => $"file '{f.FullName.Replace("'", "\\'")}'");
                await File.WriteAllLinesAsync(concatListPath, lines);

                // Build audio concat lists based on selected video segments
                string concatSysListPath = Path.Combine(_segmentDir, "concat_list_sys.txt");
                string concatMicListPath = Path.Combine(_segmentDir, "concat_list_mic.txt");
                
                bool hasSys = false;
                bool hasMic = false;

                if (_recordAudio)
                {
                    bool anySysFileExists = clippedSegments.Any(seg => File.Exists(Path.Combine(_segmentDir, $"alpnet_seg_sys_{seg.Name.Replace("alpnet_seg_", "").Replace(".mp4", "")}.wav")));
                    if (anySysFileExists)
                    {
                        var sysLines = new List<string>();
                        string silentSegment = EnsureSilentSegment(SEGMENT_DURATION_SECONDS);
                        foreach (var seg in clippedSegments)
                        {
                            string numStr = seg.Name.Replace("alpnet_seg_", "").Replace(".mp4", "");
                            string audioFile = Path.Combine(_segmentDir, $"alpnet_seg_sys_{numStr}.wav");
                            if (File.Exists(audioFile))
                            {
                                sysLines.Add($"file '{audioFile.Replace("'", "\\'")}'");
                            }
                            else
                            {
                                sysLines.Add($"file '{silentSegment.Replace("'", "\\'")}'");
                            }
                        }
                        if (sysLines.Count > 0)
                        {
                            await File.WriteAllLinesAsync(concatSysListPath, sysLines);
                            hasSys = true;
                        }
                    }
                }

                if (_recordMic)
                {
                    bool anyMicFileExists = clippedSegments.Any(seg => File.Exists(Path.Combine(_segmentDir, $"alpnet_seg_mic_{seg.Name.Replace("alpnet_seg_", "").Replace(".mp4", "")}.wav")));
                    if (anyMicFileExists)
                    {
                        var micLines = new List<string>();
                        string silentSegment = EnsureSilentSegment(SEGMENT_DURATION_SECONDS);
                        foreach (var seg in clippedSegments)
                        {
                            string numStr = seg.Name.Replace("alpnet_seg_", "").Replace(".mp4", "");
                            string audioFile = Path.Combine(_segmentDir, $"alpnet_seg_mic_{numStr}.wav");
                            if (File.Exists(audioFile))
                            {
                                micLines.Add($"file '{audioFile.Replace("'", "\\'")}'");
                            }
                            else
                            {
                                micLines.Add($"file '{silentSegment.Replace("'", "\\'")}'");
                            }
                        }
                        if (micLines.Count > 0)
                        {
                            await File.WriteAllLinesAsync(concatMicListPath, micLines);
                            hasMic = true;
                        }
                    }
                }

                // Output file path
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string outputFile = Path.Combine(saveFolder, $"AlpNet_Klip_{timestamp}.mp4");

                // Concat segments using FFmpeg
                string args = $"-f concat -safe 0 -i \"{concatListPath}\" ";
                if (hasSys) args += $"-f concat -safe 0 -i \"{concatSysListPath}\" ";
                if (hasMic) args += $"-f concat -safe 0 -i \"{concatMicListPath}\" ";

                args += "-c:v copy ";
                if (hasSys || hasMic) args += "-c:a aac -b:a 192k ";

                // Add mappings
                args += "-map 0:v "; // Video is always input 0
                if (hasSys && hasMic)
                {
                    args += "-filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[aout]\" -map \"[aout]\" ";
                }
                else if (hasSys)
                {
                    args += "-map 1:a "; // input 1 is sys
                }
                else if (hasMic)
                {
                    args += "-map 1:a "; // input 1 is mic
                }

                args += $"-shortest -y \"{outputFile}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                StatusChanged?.Invoke("Klip kaydediliyor...");

                using var concatProcess = Process.Start(psi);
                if (concatProcess != null)
                {
                    string errorOut = await concatProcess.StandardError.ReadToEndAsync();
                    await concatProcess.WaitForExitAsync();

                    if (concatProcess.ExitCode == 0 && File.Exists(outputFile))
                    {
                        // Clean up concat list
                        try { File.Delete(concatListPath); } catch { }
                        try { File.Delete(concatSysListPath); } catch { }
                        try { File.Delete(concatMicListPath); } catch { }

                        long fileSizeMB = new FileInfo(outputFile).Length / (1024 * 1024);
                        StatusChanged?.Invoke($"Klip kaydedildi! ({fileSizeMB} MB)");
                        ClipSaved?.Invoke(outputFile);
                        return outputFile;
                    }
                    else
                    {
                        ErrorOccurred?.Invoke($"Klip kaydedilemedi (Kod: {concatProcess.ExitCode}). Log: {errorOut}");
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Klip kaydedilirken hata: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Updates the buffer duration (in minutes).
        /// </summary>
        public void SetBufferMinutes(int minutes)
        {
            _bufferMinutes = minutes;
            // Immediately clean up segments that exceed the new buffer
            CleanOldSegments();
        }

        #region Private Methods

        private string FindFFmpeg(string customPath)
        {
            // 1. Check custom path
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
                return customPath;

            // 2. Check app directory
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string localFfmpeg = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(localFfmpeg)) return localFfmpeg;

            // 3. Check AlpNet data folder
            string alpnetFfmpeg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlpNet", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(alpnetFfmpeg)) return alpnetFfmpeg;

            // 4. Check PATH
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        string firstLine = output.Split('\n')[0].Trim();
                        if (File.Exists(firstLine)) return firstLine;
                    }
                }
            }
            catch { }

            return "";
        }

        private async Task<string> DownloadFFmpegAsync()
        {
            try
            {
                string ffmpegDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AlpNet", "ffmpeg");

                if (!Directory.Exists(ffmpegDir))
                    Directory.CreateDirectory(ffmpegDir);

                string ffmpegExe = Path.Combine(ffmpegDir, "ffmpeg.exe");

                // Download FFmpeg essentials from BtbN (widely used, reliable builds)
                string downloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                string zipPath = Path.Combine(ffmpegDir, "ffmpeg_download.zip");
                
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var fs = new FileStream(zipPath, FileMode.Create))
                {
                    await stream.CopyToAsync(fs);
                }

                // Extract just the ffmpeg.exe from the zip
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.ExtractToFile(ffmpegExe, true);
                            break;
                        }
                    }
                }

                // Clean up zip file
                try { File.Delete(zipPath); } catch { }

                if (File.Exists(ffmpegExe)) return ffmpegExe;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"FFmpeg indirilemedi: {ex.Message}");
            }

            return "";
        }

        private async Task<string> DetectBestEncoderAsync()
        {
            if (string.IsNullOrEmpty(_ffmpegPath)) return "libx264";

            // Try NVENC (NVIDIA)
            if (await TestEncoderAsync("h264_nvenc")) return "h264_nvenc";

            // Try AMF (AMD)
            if (await TestEncoderAsync("h264_amf")) return "h264_amf";

            // Try QSV (Intel)
            if (await TestEncoderAsync("h264_qsv")) return "h264_qsv";

            // Fallback to software encoder
            return "libx264";
        }

        private async Task<bool> TestEncoderAsync(string encoder)
        {
            try
            {
                // Test if the encoder is available by encoding a small test frame
                string args = $"-f lavfi -i nullsrc=s=256x256:d=0.1 -c:v {encoder} -f null -";
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    return proc.ExitCode == 0;
                }
            }
            catch { }
            return false;
        }

        private void CleanupTimerCallback(object? state)
        {
            CleanOldSegments();
        }

        private void CleanOldSegments()
        {
            try
            {
                if (!Directory.Exists(_segmentDir)) return;

                var segmentFiles = new DirectoryInfo(_segmentDir)
                    .GetFiles("alpnet_seg_*.mp4")
                    .OrderBy(f => f.CreationTime)
                    .ToList();

                int maxSegments = (_bufferMinutes * 60) / SEGMENT_DURATION_SECONDS + 2; // +2 for safety margin

                if (segmentFiles.Count > maxSegments)
                {
                    int toDelete = segmentFiles.Count - maxSegments;
                    for (int i = 0; i < toDelete; i++)
                    {
                        try 
                        { 
                            string numStr = segmentFiles[i].Name.Replace("alpnet_seg_", "").Replace(".mp4", "");
                            string sysFile = Path.Combine(_segmentDir, $"alpnet_seg_sys_{numStr}.wav");
                            string micFile = Path.Combine(_segmentDir, $"alpnet_seg_mic_{numStr}.wav");
                            if (File.Exists(sysFile)) try { File.Delete(sysFile); } catch { }
                            if (File.Exists(micFile)) try { File.Delete(micFile); } catch { }
                            
                            segmentFiles[i].Delete(); 
                        } 
                        catch { }
                    }
                }
            }
            catch { }
        }

        private string EnsureSilentSegment(int durationSeconds)
        {
            string silenceFile = Path.Combine(_segmentDir, $"alpnet_seg_silence_{durationSeconds}s.wav");
            if (!File.Exists(silenceFile))
            {
                try
                {
                    var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                    using var writer = new WaveFileWriter(silenceFile, format);
                    byte[] silence = new byte[format.AverageBytesPerSecond * durationSeconds];
                    writer.Write(silence, 0, silence.Length);
                }
                catch { }
            }
            return silenceFile;
        }

        private void CleanAllSegments()
        {
            try
            {
                if (!Directory.Exists(_segmentDir)) return;
                foreach (var file in Directory.GetFiles(_segmentDir, "alpnet_seg_*.mp4"))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var file in Directory.GetFiles(_segmentDir, "alpnet_seg_sys_*.wav"))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var file in Directory.GetFiles(_segmentDir, "alpnet_seg_mic_*.wav"))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var file in Directory.GetFiles(_segmentDir, "alpnet_seg_audio_*.wav"))
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (var file in Directory.GetFiles(_segmentDir, "alpnet_seg_silence_*.wav"))
                {
                    try { File.Delete(file); } catch { }
                }
                // Also clean concat list files if present
                foreach (var f in Directory.GetFiles(_segmentDir, "concat_list*.txt"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        private void MonitorFFmpegOutput()
        {
            try
            {
                if (_ffmpegProcess?.StandardError == null) return;
                
                string logPath = Path.Combine(_segmentDir, "ffmpeg_log.txt");

                while (!_ffmpegProcess.HasExited)
                {
                    string? line = _ffmpegProcess.StandardError.ReadLine();
                    if (line != null)
                    {
                        File.AppendAllText(logPath, line + Environment.NewLine);
                        if ((line.Contains("Error") || line.Contains("error")) && 
                            (line.Contains("Could not") || line.Contains("Invalid") || line.Contains("failed")))
                        {
                            ErrorOccurred?.Invoke($"FFmpeg: {line}");
                        }
                    }
                }

                // If process exited unexpectedly while recording
                if (_isRecording)
                {
                    string exitInfo = $"FFmpeg Beklenmedik Şekilde Kapandı! Kod: {_ffmpegProcess.ExitCode}";
                    File.AppendAllText(logPath, exitInfo + Environment.NewLine);

                    // If ddagrab failed, attempt automatic fallback to gdigrab
                    if (!_useFallbackGdi)
                    {
                        _useFallbackGdi = true;
                        _isRecording = false;
                        Task.Run(() => StartRecording(_bufferMinutes, _fps, _bitrate, _recordAudio, _recordMic, _micDeviceName));
                        return;
                    }

                    ErrorOccurred?.Invoke(exitInfo);
                    StopRecording(false);
                    _isRecording = false;
                    RecordingStateChanged?.Invoke(false);
                    StatusChanged?.Invoke("Kayıt beklenmedik şekilde durdu.");
                }
            }
            catch { }
        }

        #endregion

        /// <summary>
        /// Gets list of saved clips in a folder.
        /// </summary>
        public List<ClipInfo> GetSavedClips(string saveFolder)
        {
            var clips = new List<ClipInfo>();
            try
            {
                if (!Directory.Exists(saveFolder)) return clips;

                var files = new DirectoryInfo(saveFolder)
                    .GetFiles("AlpNet_Klip_*.mp4")
                    .OrderByDescending(f => f.CreationTime)
                    .Take(20); // Max 20 recent clips

                foreach (var file in files)
                {
                    clips.Add(new ClipInfo
                    {
                        FileName = file.Name,
                        FullPath = file.FullName,
                        SizeMB = (int)(file.Length / (1024 * 1024)),
                        CreatedAt = file.CreationTime
                    });
                }
            }
            catch { }
            return clips;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                StopRecording(true);
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~FFmpegManager()
        {
            Dispose();
        }
    }

    public class ClipInfo
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public int SizeMB { get; set; }
        public DateTime CreatedAt { get; set; }
        public string DisplaySize => $"{SizeMB} MB";
        public string DisplayDate => CreatedAt.ToString("dd.MM.yyyy HH:mm:ss");
    }
}
