using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;

namespace AlpNetPulse
{
    public partial class MainWindow
    {
        private Process? _translatorProcess;
        private Process? _reverseTranslatorProcess;
        private bool _isTranslating = false;

        private bool _isLoadingVoiceDevices = false;

        private void UnsubscribeVoiceEvents()
        {
            CmbVoiceMic.SelectionChanged -= VoiceSettings_SelectionChanged;
            CmbVoiceSourceLang.SelectionChanged -= VoiceSettings_SelectionChanged;
            CmbVoiceTargetLang.SelectionChanged -= VoiceSettings_SelectionChanged;
            CmbVoiceOut.SelectionChanged -= VoiceSettings_SelectionChanged;
            CmbReverseSource.SelectionChanged -= VoiceSettings_SelectionChanged;
            CmbReverseOutput.SelectionChanged -= VoiceSettings_SelectionChanged;
            ChkReverseTranslation.Click -= VoiceSettings_CheckClicked;
            ChkReverseTTS.Click -= VoiceSettings_CheckClicked;
        }

        private void SubscribeVoiceEvents()
        {
            UnsubscribeVoiceEvents();
            CmbVoiceMic.SelectionChanged += VoiceSettings_SelectionChanged;
            CmbVoiceSourceLang.SelectionChanged += VoiceSettings_SelectionChanged;
            CmbVoiceTargetLang.SelectionChanged += VoiceSettings_SelectionChanged;
            CmbVoiceOut.SelectionChanged += VoiceSettings_SelectionChanged;
            CmbReverseSource.SelectionChanged += VoiceSettings_SelectionChanged;
            CmbReverseOutput.SelectionChanged += VoiceSettings_SelectionChanged;
            ChkReverseTranslation.Click += VoiceSettings_CheckClicked;
            ChkReverseTTS.Click += VoiceSettings_CheckClicked;
        }

        private void LoadVoiceDevices()
        {
            try
            {
                _isLoadingVoiceDevices = true;
                UnsubscribeVoiceEvents();

                CmbVoiceMic.Items.Clear();
                CmbVoiceMic.Items.Add(new ComboBoxItem { Content = "Varsayılan Mikrofon" });
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    CmbVoiceMic.Items.Add(new ComboBoxItem { Content = WaveIn.GetCapabilities(i).ProductName });
                }
                CmbVoiceMic.SelectedIndex = 0;

                CmbVoiceOut.Items.Clear();
                CmbVoiceOut.Items.Add(new ComboBoxItem { Content = "Varsayılan Hoparlör" });
                CmbReverseSource.Items.Clear();
                CmbReverseSource.Items.Add(new ComboBoxItem { Content = "Varsayılan Hoparlör" });
                CmbReverseOutput.Items.Clear();
                CmbReverseOutput.Items.Add(new ComboBoxItem { Content = "Varsayılan Hoparlör" });
                
                int selectedOutIndex = 0;
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    var productName = WaveOut.GetCapabilities(i).ProductName;
                    CmbVoiceOut.Items.Add(new ComboBoxItem { Content = productName });
                    CmbReverseSource.Items.Add(new ComboBoxItem { Content = productName });
                    CmbReverseOutput.Items.Add(new ComboBoxItem { Content = productName });
                    if (productName.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        selectedOutIndex = i + 1;
                    }
                }
                
                var settings = PulseAppSettings.Load();
                CmbVoiceSourceLang.SelectedIndex = settings.VoiceSourceLangIndex >= 0 && settings.VoiceSourceLangIndex < CmbVoiceSourceLang.Items.Count ? settings.VoiceSourceLangIndex : 0;
                CmbVoiceTargetLang.SelectedIndex = settings.VoiceTargetLangIndex >= 0 && settings.VoiceTargetLangIndex < CmbVoiceTargetLang.Items.Count ? settings.VoiceTargetLangIndex : 1;
                CmbVoiceOut.SelectedIndex = settings.VoiceOutIndex > 0 && settings.VoiceOutIndex < CmbVoiceOut.Items.Count ? settings.VoiceOutIndex : selectedOutIndex;
                CmbReverseSource.SelectedIndex = settings.ReverseSourceIndex >= 0 && settings.ReverseSourceIndex < CmbReverseSource.Items.Count ? settings.ReverseSourceIndex : 0;
                CmbReverseOutput.SelectedIndex = settings.ReverseOutputIndex >= 0 && settings.ReverseOutputIndex < CmbReverseOutput.Items.Count ? settings.ReverseOutputIndex : 0;
                ChkReverseTranslation.IsChecked = settings.ReverseTranslationActive;
                ChkReverseTTS.IsChecked = settings.ReverseTTSActive;

                SubscribeVoiceEvents();
            }
            catch (Exception ex)
            {
                AppendVoiceLog($"[HATA] Cihazlar yüklenemedi: {ex.Message}");
            }
            finally
            {
                _isLoadingVoiceDevices = false;
            }
        }

        private async void VoiceSettings_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingVoiceDevices || !this.IsLoaded) return;
            var s2 = PulseAppSettings.Load();
            s2.VoiceSourceLangIndex = CmbVoiceSourceLang.SelectedIndex;
            s2.VoiceTargetLangIndex = CmbVoiceTargetLang.SelectedIndex;
            s2.VoiceOutIndex = CmbVoiceOut.SelectedIndex;
            s2.ReverseSourceIndex = CmbReverseSource.SelectedIndex;
            s2.ReverseOutputIndex = CmbReverseOutput.SelectedIndex;
            s2.Save();
            
            if (_isTranslating)
            {
                BtnVoiceStart_Click(null, null);
                await Task.Delay(800);
                if (!_isTranslating) BtnVoiceStart_Click(null, null);
            }
        }

        private async void VoiceSettings_CheckClicked(object? sender, RoutedEventArgs e)
        {
            if (_isLoadingVoiceDevices || !this.IsLoaded) return;
            var s2 = PulseAppSettings.Load();
            s2.ReverseTranslationActive = ChkReverseTranslation.IsChecked == true;
            s2.ReverseTTSActive = ChkReverseTTS.IsChecked == true;
            s2.Save();

            if (_isTranslating)
            {
                BtnVoiceStart_Click(null, null);
                await Task.Delay(800);
                if (!_isTranslating) BtnVoiceStart_Click(null, null);
            }
        }

        private async void BtnVoiceStart_Click(object? sender, RoutedEventArgs? e)
        {
            if (_isTranslating)
            {
                StopTranslator();
            }
            else
            {
                await StartTranslator();
            }
        }

        private async Task CheckAndInstallVirtualCable()
        {
            bool cableFound = false;
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                if (WaveOut.GetCapabilities(i).ProductName.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cableFound = true;
                    break;
                }
            }

            if (!cableFound)
            {
                AppendVoiceLog("Sanal kablo eksik. Güvenli ve gizli modda arka planda kuruluyor...");
                
                string tempDir = Path.Combine(Path.GetTempPath(), "AlpNetVBCable");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                string zipPath = Path.Combine(tempDir, "VBCable.zip");
                if (!File.Exists(Path.Combine(tempDir, "VBCABLE_Setup_x64.exe")))
                {
                    try
                    {
                        using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("AlpNetPulse.Resources.VBCable.zip"))
                        {
                            if (stream != null)
                            {
                                using (var fileStream = new FileStream(zipPath, FileMode.Create))
                                {
                                    stream.CopyTo(fileStream);
                                }
                                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempDir, true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendVoiceLog($"[HATA] VBCable zip çıkartılamadı: {ex.Message}");
                    }
                }

                string installerPath = Path.Combine(tempDir, "VBCABLE_Setup_x64.exe");
                if (File.Exists(installerPath))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = installerPath,
                            Arguments = "-i -h",
                            Verb = "runas",
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true
                        };
                        var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            await proc.WaitForExitAsync();
                            AppendVoiceLog("Sanal kablo başarıyla kuruldu. Yeniden başlatmaya gerek yok!");
                            await Task.Delay(2000);
                            Dispatcher.Invoke(() => LoadVoiceDevices());
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendVoiceLog($"[HATA] Sürücü kurulumu UAC reddedildi: {ex.Message}");
                    }
                }
                else
                {
                    AppendVoiceLog("[HATA] VBCable kurulum klasörü (VBCable) bulunamadı!");
                }
            }
        }



        private async Task StartTranslator()
        {
            try
            {
                TxtVoiceLog.Clear();
                AppendVoiceLog("AlpNet Pro Çeviri sistemi başlatılıyor...");

                await CheckAndInstallVirtualCable();

                string micName = CmbVoiceMic.SelectedIndex == 0 ? "default" : (CmbVoiceMic.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";
                string outName = CmbVoiceOut.SelectedIndex == 0 ? "default" : (CmbVoiceOut.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";

                string srcLang = CmbVoiceSourceLang.SelectedIndex == 0 ? "tr" : "en";
                
                string targetLang = "en";
                if (CmbVoiceTargetLang.SelectedIndex == 1) targetLang = "ru";
                if (CmbVoiceTargetLang.SelectedIndex == 2) targetLang = "de";

                // USE THE STANDALONE EXE
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (baseDir.Contains("Temp\\.net"))
                {
                    baseDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName) ?? AppDomain.CurrentDomain.BaseDirectory;
                }
                string exePath = Path.Combine(baseDir, "translator.exe");

                if (!File.Exists(exePath))
                {
                    string debugPath = Path.Combine(baseDir, "..", "..", "..", "..", "translator.exe");
                    if (File.Exists(debugPath)) exePath = debugPath;
                }
                
                if (!File.Exists(exePath))
                {
                    string processDir = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? baseDir;
                    string processPath = Path.Combine(processDir, "translator.exe");
                    if (File.Exists(processPath)) exePath = processPath;
                }


                
                exePath = Path.GetFullPath(exePath);

                if (!File.Exists(exePath))
                {
                    AppendVoiceLog("[Bilgi] Çeviri modülü (translator.exe) indiriliyor, lütfen bekleyin (64 MB)...");
                    string translatorUrl = "https://github.com/alphankilicaslan/AlpNet/releases/download/Update/translator.exe";
                    try
                    {
                        using (var client = new System.Net.Http.HttpClient())
                        {
                            var response = await client.GetAsync(translatorUrl);
                            response.EnsureSuccessStatusCode();
                            using (var fs = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await response.Content.CopyToAsync(fs);
                            }
                        }
                        AppendVoiceLog("[Bilgi] Çeviri modülü başarıyla indirildi!");
                    }
                    catch (Exception ex)
                    {
                        AppendVoiceLog($"[Kritik HATA] translator.exe indirilemedi: {ex.Message}");
                        return;
                    }
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"forward \"{micName}\" \"{outName}\" {srcLang} {targetLang} none",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                _translatorProcess = new Process { StartInfo = psi };

                _translatorProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Dispatcher.Invoke(() => ParseAndAppendVoiceLog(e.Data));
                    }
                };

                _translatorProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && !e.Data.Contains("SoundcardRuntimeWarning"))
                    {
                        Dispatcher.Invoke(() => ParseAndAppendVoiceLog(e.Data, "[MOTOR] "));
                    }
                };

                _translatorProcess.Start();
                _translatorProcess.BeginOutputReadLine();
                _translatorProcess.BeginErrorReadLine();

                _isTranslating = true;
                BtnVoiceStart.Content = "Çeviriyi Durdur";
                BtnVoiceStart.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68)); // Red color
                BtnVoiceStart.Foreground = System.Windows.Media.Brushes.White;

                if (ChkReverseTranslation.IsChecked == true)
                {
                    if (File.Exists(exePath))
                    {
                        string revSource = "default";
                        if (CmbReverseSource.SelectedIndex > 0 && CmbReverseSource.SelectedItem is ComboBoxItem itemSrc)
                        {
                            revSource = itemSrc.Content?.ToString() ?? "default";
                        }
                        
                        string revOutput = "default";
                        if (CmbReverseOutput.SelectedIndex > 0 && CmbReverseOutput.SelectedItem is ComboBoxItem itemOut)
                        {
                            revOutput = itemOut.Content?.ToString() ?? "default";
                        }
                        
                        string ttsArg = (ChkReverseTTS.IsChecked == true) ? "true" : "false";

                        var rpsi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = $"reverse {targetLang} {srcLang} \"{revSource}\" {ttsArg} \"{revOutput}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            StandardOutputEncoding = System.Text.Encoding.UTF8,
                            StandardErrorEncoding = System.Text.Encoding.UTF8
                        };

                        _reverseTranslatorProcess = new Process { StartInfo = rpsi };
                        _reverseTranslatorProcess.OutputDataReceived += (s, ev) =>
                        {
                            if (!string.IsNullOrEmpty(ev.Data))
                            {
                                Dispatcher.Invoke(() => ParseAndAppendVoiceLog(ev.Data, "[GERİ] "));
                            }
                        };
                        _reverseTranslatorProcess.ErrorDataReceived += (s, ev) =>
                        {
                            if (!string.IsNullOrEmpty(ev.Data) && !ev.Data.Contains("SoundcardRuntimeWarning"))
                            {
                                Dispatcher.Invoke(() => ParseAndAppendVoiceLog(ev.Data, "[GERİ MOTOR] "));
                            }
                        };
                        _reverseTranslatorProcess.Start();
                        _reverseTranslatorProcess.BeginOutputReadLine();
                        _reverseTranslatorProcess.BeginErrorReadLine();
                    }
                    else
                    {
                        AppendVoiceLog("[Uyarı] translator.exe bulunamadı, geri çeviri başlatılamadı.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendVoiceLog($"[HATA] {ex.Message}");
                StopTranslator();
            }
        }

        private void StopTranslator()
        {
            if (_translatorProcess != null)
            {
                try
                {
                    if (!_translatorProcess.HasExited)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/F /T /PID {_translatorProcess.Id}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        })?.WaitForExit();
                    }
                    
                    // Zombie temizliği
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM translator.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();
                }
                catch { } // Ignore all process state exceptions if it failed to start
            }

            if (_reverseTranslatorProcess != null)
            {
                try
                {
                    if (!_reverseTranslatorProcess.HasExited)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/F /T /PID {_reverseTranslatorProcess.Id}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        })?.WaitForExit();
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM translator.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();
                }
                catch { }
            }

            _isTranslating = false;
            BtnVoiceStart.Content = "Çeviriyi Başlat";
            BtnVoiceStart.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
            BtnVoiceStart.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));
            AppendVoiceLog("Çeviri sistemi durduruldu.");
        }

        private void ParseAndAppendVoiceLog(string data, string prefix = "")
        {
            try
            {
                if (data.StartsWith("{"))
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(data))
                    {
                        var root = doc.RootElement;
                        string type = root.TryGetProperty("type", out var typeElem) ? typeElem.GetString() ?? "" : "";
                        string message = root.TryGetProperty("message", out var msgElem) ? msgElem.GetString() ?? "" : "";

                        if (type == "system")
                            AppendVoiceLog($"{prefix}[SİSTEM] {message}");
                        else if (type == "user")
                            AppendVoiceLog($"{prefix}[SEN] {message}");
                        else if (type == "translation")
                            AppendVoiceLog($"{prefix}[ÇEVİRİ] {message}");
                        else if (type == "error" || type == "fatal")
                            AppendVoiceLog($"{prefix}[HATA] {message}");
                        else
                            AppendVoiceLog($"{prefix}{data}");
                    }
                }
                else
                {
                    AppendVoiceLog($"{prefix}{data}");
                }
            }
            catch
            {
                AppendVoiceLog($"{prefix}{data}");
            }
        }

        private void AppendVoiceLog(string message)
        {
            TxtVoiceLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            TxtVoiceLog.ScrollToEnd();
        }
    }
}
