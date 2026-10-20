using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AlpNetPulse
{
    public partial class VideoPlayerWindow : Window
    {
        private string _videoPath;
        private string _ffmpegPath;
        private DispatcherTimer _timer;
        private bool _isDragging = false;
        private bool _isPlaying = false;
        
        private TimeSpan _trimStart = TimeSpan.Zero;
        private TimeSpan _trimEnd = TimeSpan.Zero;

        public VideoPlayerWindow(string videoPath, string ffmpegPath)
        {
            InitializeComponent();
            _videoPath = videoPath;
            _ffmpegPath = ffmpegPath;

            this.SizeChanged += (s, e) => UpdateMarkers();

            VideoPlayer.Source = new Uri(_videoPath);
            VideoPlayer.Play();
            _isPlaying = true;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_isDragging && VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                TimelineSlider.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                TimelineSlider.Value = VideoPlayer.Position.TotalSeconds;
                
                UpdateTimeText();
            }
        }

        private void UpdateTimeText()
        {
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                TxtTime.Text = $"{VideoPlayer.Position:mm\\:ss} / {VideoPlayer.NaturalDuration.TimeSpan:mm\\:ss}";
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                TimelineSlider.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                _trimEnd = VideoPlayer.NaturalDuration.TimeSpan;
                TxtEnd.Text = _trimEnd.ToString(@"mm\:ss");
                UpdateMarkers();
            }
        }

        private void UpdateMarkers()
        {
            if (TimelineSlider.Maximum <= 0 || TimelineCanvas.ActualWidth <= 0) return;
            
            double totalWidth = TimelineCanvas.ActualWidth;
            double startX = (_trimStart.TotalSeconds / TimelineSlider.Maximum) * totalWidth;
            double endX = (_trimEnd.TotalSeconds / TimelineSlider.Maximum) * totalWidth;

            if (startX >= 0)
            {
                RectStart.Visibility = Visibility.Visible;
                System.Windows.Controls.Canvas.SetLeft(RectStart, startX);
            }

            if (endX > startX)
            {
                RectEnd.Visibility = Visibility.Visible;
                System.Windows.Controls.Canvas.SetLeft(RectEnd, endX);

                RectSelection.Visibility = Visibility.Visible;
                System.Windows.Controls.Canvas.SetLeft(RectSelection, startX);
                RectSelection.Width = endX - startX;
            }
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Position = TimeSpan.Zero;
            VideoPlayer.Play();
        }

        private void VideoPlayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            TogglePlayPause();
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (_isPlaying)
            {
                VideoPlayer.Pause();
                BtnPlayPause.Content = "▶ Oynat";
            }
            else
            {
                VideoPlayer.Play();
                BtnPlayPause.Content = "⏸ Duraklat";
            }
            _isPlaying = !_isPlaying;
        }

        private void TimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            VideoPlayer.Pause();
        }

        private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            VideoPlayer.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
            if (_isPlaying) VideoPlayer.Play();
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDragging)
            {
                VideoPlayer.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
                UpdateTimeText();
            }
        }

        private void BtnSetStart_Click(object sender, RoutedEventArgs e)
        {
            _trimStart = VideoPlayer.Position;
            if (_trimStart >= _trimEnd) _trimStart = _trimEnd - TimeSpan.FromSeconds(1);
            TxtStart.Text = _trimStart.ToString(@"mm\:ss");
            UpdateMarkers();
        }

        private void BtnSetEnd_Click(object sender, RoutedEventArgs e)
        {
            _trimEnd = VideoPlayer.Position;
            if (_trimEnd <= _trimStart) _trimEnd = _trimStart + TimeSpan.FromSeconds(1);
            TxtEnd.Text = _trimEnd.ToString(@"mm\:ss");
            UpdateMarkers();
        }

        private async void BtnTrim_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
            {
                System.Windows.MessageBox.Show("FFmpeg bulunamadı!", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(_videoPath))
            {
                System.Windows.MessageBox.Show("Kaynak video dosyası bulunamadı!", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            double duration = _trimEnd.TotalSeconds - _trimStart.TotalSeconds;
            if (duration <= 0.05)
            {
                System.Windows.MessageBox.Show("Geçersiz kırpma aralığı! Bitiş zamanı başlangıç zamanından büyük olmalıdır.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnTrim.IsEnabled = false;
            VideoPlayer.Pause();
            BtnPlayPause.Content = "▶ Oynat";
            _isPlaying = false;

            BtnTrim.Content = "⏳ İşleniyor...";

            string outputDir = Path.GetDirectoryName(_videoPath) ?? "";
            string ext = Path.GetExtension(_videoPath);
            string name = Path.GetFileNameWithoutExtension(_videoPath);
            
            // To prevent overwriting the same file while it's locked by the player
            string outputPath = Path.Combine(outputDir, $"{name}_Trimmed_{DateTime.Now.Ticks}{ext}");
            
            // Unload the video before processing to release the file lock if we plan to delete it later
            VideoPlayer.Source = null;

            bool keepSystem = ChkKeepSystemAudio.IsChecked == true;
            bool keepMic = ChkKeepMicAudio.IsChecked == true;
            bool deleteOriginal = ChkDeleteOriginal.IsChecked == true;

            try
            {
                int exitCode = -1;
                await Task.Run(() =>
                {
                    string startStr = _trimStart.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    double duration = _trimEnd.TotalSeconds - _trimStart.TotalSeconds;
                    string durationStr = duration.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    
                    // Multi-track audio mapping logic
                    // Stream 0: Video
                    // Stream 1: System Audio (if exists)
                    // Stream 2: Mic Audio (if exists)
                    // FFmpeg map logic: -map 0:v -map 0:a:0? -map 0:a:1?
                    // We will explicitly map based on checkboxes.
                    
                    string mapArgs = "-map 0:v ";
                    if (keepSystem && keepMic)
                    {
                        mapArgs += "-map 0:a? ";
                    }
                    else if (keepSystem)
                    {
                        mapArgs += "-map 0:a:0? ";
                    }
                    else if (keepMic)
                    {
                        mapArgs += "-map 0:a:1? ";
                    }
                    
                    if (!keepSystem && !keepMic)
                    {
                        mapArgs = "-map 0:v -an ";
                    }

                    // To prevent file corruption, -ss must be BEFORE -i, and we use -t for duration.
                    // -avoid_negative_ts make_non_negative ensures the timestamps start at 0
                    string args = $"-ss {startStr} -i \"{_videoPath}\" -t {durationStr} {mapArgs} -c copy -avoid_negative_ts make_non_negative -y \"{outputPath}\"";
                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit();
                        exitCode = proc.ExitCode;
                    }
                });

                bool fileValid = File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
                if (exitCode != 0 || !fileValid)
                {
                    if (File.Exists(outputPath))
                    {
                        try { File.Delete(outputPath); } catch { }
                    }
                    // Restore original video playback
                    if (File.Exists(_videoPath))
                    {
                        try { VideoPlayer.Source = new Uri(_videoPath); } catch { }
                    }
                    throw new Exception($"FFmpeg kırpma işlemini tamamlayamadı (Çıkış kodu: {exitCode}). Orijinal dosya korundu.");
                }

                if (deleteOriginal && File.Exists(_videoPath))
                {
                    try { File.Delete(_videoPath); } catch { }
                }

                System.Windows.MessageBox.Show($"Video başarıyla kaydedildi:\n{outputPath}", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // Switch player to new video
                _videoPath = outputPath;
                VideoPlayer.Source = new Uri(_videoPath);
                VideoPlayer.Play();
                _isPlaying = true;
                BtnPlayPause.Content = "⏸ Duraklat";
            }
            catch (Exception ex)
            {
                if (VideoPlayer.Source == null && File.Exists(_videoPath))
                {
                    try { VideoPlayer.Source = new Uri(_videoPath); } catch { }
                }
                System.Windows.MessageBox.Show($"Kırpma sırasında hata:\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnTrim.IsEnabled = true;
                BtnTrim.Content = "💾 KAYDET";
            }
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            VideoPlayer.Close();
            base.OnClosed(e);
        }
    }
}
