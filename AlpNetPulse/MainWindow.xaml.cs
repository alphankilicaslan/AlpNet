using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LibreHardwareMonitor.Hardware;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using Microsoft.Win32;
using System.Management;
using WinForms = System.Windows.Forms;
using System.IO.Compression;

namespace AlpNetPulse
{
    public partial class MainWindow : Window
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        public static extern int SendARP(uint DestIP, uint SrcIP, byte[] pMacAddr, ref uint PhyAddrLen);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        private uint _taskbarCreatedMsg = 0;

        private readonly HttpClient _httpClient;
        private readonly DispatcherTimer _licenseCheckTimer;
        private readonly DispatcherTimer _statsUpdateTimer;
        private readonly DispatcherTimer _presenceTimer;
        private string _deviceId = "";
        private LicenseState _licenseState = new LicenseState();
        private readonly string _licenseDir;
        private readonly string _licenseFile;
        private WinForms.NotifyIcon _notifyIcon;
        private WinForms.ToolStripMenuItem? _itemBrowserVpn;
        private readonly SemaphoreSlim _browserVpnLock = new SemaphoreSlim(1, 1);

        // Instant Replay System
        private FFmpegManager? _ffmpegManager;
        private GlobalHotkeyManager? _hotkeyManager;
        private ReplaySettings _replaySettings = new ReplaySettings();
        private DispatcherTimer? _replayDiskTimer;

        private readonly DispatcherTimer _appStateTimer;
        private string _lastBroadcastMsg = "";
        
        private string LastBroadcastMsg
        {
            get
            {
                try
                {
                    var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlpNet", ".lastmsg");
                    if (System.IO.File.Exists(path)) return System.IO.File.ReadAllText(path);
                } catch { }
                return _lastBroadcastMsg;
            }
            set
            {
                _lastBroadcastMsg = value;
                try
                {
                    var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlpNet");
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, ".lastmsg"), value);
                } catch { }
            }
        }

        private string _updateUrl = "";
        private string _remoteVersion = "";
        private bool _versionBannerShown = false;

        // LibreHardwareMonitor
        private Computer? _computer;
        private System.Windows.Threading.DispatcherTimer? _hwTimer;


        // Uygulamanın KENDİ sürümü (Her Güncellemede burayı artırın)
        private const string BASE_VERSION = "2.2.9"; // Kodun orjinal versiyonu

        private string CleanVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "";
            return v.Trim().TrimStart('v', 'V');
        }

        private string CurrentVersion => CleanVersion(BASE_VERSION);

        private DiscordRpcManager? _discordRpc;

        private void UpdateDiscordPresence()
        {
            try
            {
                string status = _isBypassActive ? "⚡ Akıllı Bypass (0 Ping) Aktif" : "⚪ AlpNet Pulse Hazır";
                string details = _isGameModeEnabled ? "🎮 Oyun Modu: AÇIK" : $"🚀 v{BASE_VERSION}";
                _discordRpc?.UpdatePresence(status, details);
            }
            catch { }
        }

        public MainWindow()
        {
            InitializeComponent();
            
            try
            {
                _discordRpc = new DiscordRpcManager();
                _discordRpc.Start();
                UpdateDiscordPresence();
            }
            catch { }
            
            try
            {
                _hwTimer = new System.Windows.Threading.DispatcherTimer();
                _hwTimer.Interval = TimeSpan.FromSeconds(4); // Increased to 4s to drastically reduce RAM/CPU usage
                _hwTimer.Tick += HwTimer_Tick;
                _hwTimer.Start();
            }
            catch { }
            
            LblAppVersion.Text = $"v{CurrentVersion}";

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _httpClient.DefaultRequestHeaders.Add("Bypass-Tunnel-Reminder", "true");
            _httpClient.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "1");
            _httpClient.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            
            Task.Run(() =>
            {
                CheckAndUpdateWifiVisibility();
            });
            
            // Set up licensing paths
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _licenseDir = Path.Combine(appData, "AlpNet");
            _licenseFile = Path.Combine(_licenseDir, "license_config.json");

            if (!Directory.Exists(_licenseDir))
            {
                Directory.CreateDirectory(_licenseDir);
            }

            // Timers
            _licenseCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _licenseCheckTimer.Tick += LicenseCheckTimer_Tick;

            _statsUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statsUpdateTimer.Tick += StatsUpdateTimer_Tick;

            _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) }; // Orijinal 15s -> 60s
            _presenceTimer.Tick += PresenceTimer_Tick;

            _appStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; // 3 saniye ile anlık canlı duyuru, bakım modu ve özel mesaj
            _appStateTimer.Tick += AppStateTimer_Tick;

            _notifyIcon = new WinForms.NotifyIcon
            {
                Text = "AlpNet Pulse",
                Visible = true
            };

            var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/alpnet_icon.ico"))?.Stream;
            if (iconStream != null)
            {
                _notifyIcon.Icon = new System.Drawing.Icon(iconStream);
            }
            else
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            _notifyIcon.DoubleClick += (s, ev) => 
            {
                ShowInTaskbar = true;
                Show();
                WindowState = WindowState.Normal;
            };

            var contextMenu = new WinForms.ContextMenuStrip();
            var itemBypass = new WinForms.ToolStripMenuItem("⚡ Akıllı Bypass: KAPALI", null, (s, ev) => ToggleBypass_Click(null, null));
            _itemBrowserVpn = new WinForms.ToolStripMenuItem("🌐 Browser VPN: KAPALI", null, async (s, ev) =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await ToggleBrowserVpnAsync();
                });
            });

            contextMenu.Opening += (s, ev) =>
            {
                itemBypass.Checked = _isBypassActive;
                itemBypass.Text = _isBypassActive ? "⚡ Akıllı Bypass: AÇIK ✅" : "⚡ Akıllı Bypass: KAPALI ❌";
                bool isVpnActive = ChkBrowserVpn.IsChecked == true;
                if (_itemBrowserVpn != null)
                {
                    _itemBrowserVpn.Checked = isVpnActive;
                    _itemBrowserVpn.Text = isVpnActive ? "🌐 Browser VPN: AÇIK ✅" : "🌐 Browser VPN: KAPALI ❌";
                }
            };

            contextMenu.Items.Add(itemBypass);
            contextMenu.Items.Add(_itemBrowserVpn);
            contextMenu.Items.Add(new WinForms.ToolStripSeparator());
            contextMenu.Items.Add("Göster", null, (s, ev) => { ShowInTaskbar = true; Show(); WindowState = WindowState.Normal; });
            contextMenu.Items.Add("Kapat", null, (s, ev) => Dispatcher.Invoke(this.Close));
            _notifyIcon.ContextMenuStrip = contextMenu;

            System.Windows.Application.Current.Exit += (s, e) => PerformShutdownCleanup();

            Loaded += MainWindow_Loaded;
        }

        private async System.Threading.Tasks.Task FetchInitialState()
        {
            try
            {
                string firebaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/appstate.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    var response = await _httpClient.GetAsync(firebaseUrl, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync(cts.Token);
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var state = JsonSerializer.Deserialize<AppState>(json);
                            if (state != null && !string.IsNullOrWhiteSpace(state.version) && !string.IsNullOrWhiteSpace(state.updateUrl))
                            {
                                string remoteVerStr = CleanVersion(state.version);
                                string currentVerStr = CleanVersion(BASE_VERSION);

                                if (Version.TryParse(remoteVerStr, out Version? remoteVer) && Version.TryParse(currentVerStr, out Version? currentVer))
                                {
                                    if (remoteVer > currentVer)
                                    {
                                        _remoteVersion = remoteVerStr;
                                        _updateUrl = state.updateUrl;
                                        await PerformUpdateAndRestartAsync(_updateUrl, _remoteVersion);
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void CleanupOldFiles()
        {
            try
            {
                string currentExeDir = AppDomain.CurrentDomain.BaseDirectory;
                var oldFiles = System.IO.Directory.GetFiles(currentExeDir, "*.old.exe");
                foreach (var oldFile in oldFiles)
                {
                    try { System.IO.File.Delete(oldFile); } catch { } 
                }

                // Temp klasöründeki eski güncellemeleri de sil
                var tempPath = System.IO.Path.GetTempPath();
                var oldTempFiles = System.IO.Directory.GetFiles(tempPath, "AlpNetPulse_Old_*.exe");
                foreach (var tempFile in oldTempFiles)
                {
                    try { System.IO.File.Delete(tempFile); } catch { }
                }
            }
            catch { }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            WarpManager.PurgeDpiServices();
            CleanupOldFiles();

            // Initial loader state on texts
            CardSpeedVal.Text = "Hesaplanıyor...";
            CardStandardVal.Text = "Analiz Ediliyor...";
            CardGatewayVal.Text = "Taranıyor...";
            CardHealthVal.Text = "Taranıyor...";

            // Initialize Device ID
            await InitDeviceId();
            TxtDeviceId.Text = _deviceId;
            PageLicenseHwid.Text = _deviceId;
            AlpNetTelemetryService.Instance.DeviceId = _deviceId;
            AlpNetTelemetryService.Instance.Info("SİSTEM", $"AlpNet Pulse arayüzü yüklendi. HWID: {_deviceId}");

            // Load and verify license
            await LoadLicenseConfig();
            
            await FetchInitialState();
            // UpdateOverlay check removed

            CheckLicenseAndVerify();

            _licenseCheckTimer.Start();
            _statsUpdateTimer.Start();
            _presenceTimer.Start();
            _appStateTimer.Start();
            PresenceTimer_Tick(null, null);
            AppStateTimer_Tick(null, null);

            // Initialize Instant Replay System
            await InitializeReplaySystemAsync();

            // Load and apply DNS & Bypass settings
            try
            {
                var settings = PulseAppSettings.Load();

                // Browser VPN Persistence
                ChkBrowserVpn.IsChecked = settings.BrowserVpnEnabled;
                _warpManager.IsBrowserVpnEnabled = settings.BrowserVpnEnabled;
                UpdateBrowserVpnUI(settings.BrowserVpnEnabled);

                if (settings.AutoDnsMode != "dhcp")
                {
                    if (settings.AutoDnsMode == "cloudflare") SetDnsCloudflare_Click(null, null);
                    else if (settings.AutoDnsMode == "google") SetDnsGoogle_Click(null, null);
                    else if (settings.AutoDnsMode == "doh" && !_isDoHActive) ToggleDoH_Click(null, null);
                }

                // Otomatik WARP Split Tüneli Başlat (Ayarlarda açıksa otomatik bağlanır)
                if (settings.AutoBypassEnabled && !_isBypassActive)
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                                break;
                            await System.Threading.Tasks.Task.Delay(300);
                        }
                        await System.Threading.Tasks.Task.Delay(200);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (settings.AutoBypassEnabled && !_isBypassActive)
                            {
                                ToggleBypass_Click(null, null);
                            }
                        });
                    });
                }
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                // Windows açılışında veya explorer.exe yeniden başladığında sistem tepsisi (System Tray)
                // simgesinin kaybolmasını kalıcı olarak engelleyen TaskbarCreated hook'u:
                _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
                var hwndSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                hwndSource?.AddHook(WndProc);
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
            {
                // Explorer yeniden başladı, tepsiyi anında yeniden canlandır
                try
                {
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.Visible = false;
                        _notifyIcon.Visible = true;
                    }
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        #region UI Handlers
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
            
            // RAM Optimizasyonu: Uygulama simge DURUMUna küçültüldüğünde belleği boşalt
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                var process = Process.GetCurrentProcess();
                process.MinWorkingSet = (IntPtr)(1024 * 1024);
            }
            catch { }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                StopWifiAutoScanAndRadarAnimation();
                
                // RAM Optimizasyonu
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    var process = Process.GetCurrentProcess();
                    process.MinWorkingSet = (IntPtr)(1024 * 1024);
                }
                catch { }
            }
            else if (WindowState == WindowState.Normal || WindowState == WindowState.Maximized)
            {
                if (PageWifi.Visibility == Visibility.Visible && IsVisible)
                {
                    StartWifiAutoScanAndRadarAnimation();
                }
            }
        }

        private async void AppStateTimer_Tick(object? sender, EventArgs? e)
        {
            // Bağımsız Wi-Fi donanımı ve açık/kapalı durumu kontrolü
            _ = Task.Run(() => CheckAndUpdateWifiVisibility());

            try
            {
                // 1. Genel AppState kontrolü
                string stateUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/appstate.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                foreach (var client in new[] { _httpClient })
                {
                    var response = await client.GetAsync(stateUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var state = JsonSerializer.Deserialize<AppState>(json);
                            if (state != null)
                            {
                                // Maintenance
                                if (state.maintenance)
                                {
                                    TxtMaintenanceMsg.Text = string.IsNullOrEmpty(state.maintenanceMsg) ? "SİSTEM bakımda." : state.maintenanceMsg;
                                    MaintenanceOverlay.Visibility = Visibility.Visible;
                                    LicenseOverlay.Visibility = Visibility.Collapsed;
                                    MainDashboard.Visibility = Visibility.Collapsed;
                                    return; 
                                }
                                else
                                {
                                    MaintenanceOverlay.Visibility = Visibility.Collapsed;
                                    
                                    if (_licenseState.Licensed) ShowDashboard();
                                    else ShowLicenseOverlay();
                                }

                                // Broadcast Msg
                                if (!string.IsNullOrEmpty(state.broadcastMsg) && state.broadcastMsg != LastBroadcastMsg)
                                {
                                    LastBroadcastMsg = state.broadcastMsg;
                                    ShowInAppMessage(state.broadcastMsg);
                                }

                                // Update Notification (Runtime)
                                if (!string.IsNullOrWhiteSpace(state.version) && !string.IsNullOrWhiteSpace(state.updateUrl) && Version.TryParse(CleanVersion(state.version), out Version? rVer) && Version.TryParse(CleanVersion(BASE_VERSION), out Version? cVer) && rVer > cVer && !_versionBannerShown)
                                {
                                    _versionBannerShown = true;
                                    _remoteVersion = CleanVersion(state.version);
                                    _updateUrl = state.updateUrl;
                                    _ = PerformUpdateAndRestartAsync(_updateUrl, _remoteVersion);
                                }
                            }
                        }
                    }

                    // 2. Özel Mesaj Kontrolü
                    if (!string.IsNullOrEmpty(_deviceId))
                    {
                        string dmUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/messages/{_deviceId}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                        var dmResponse = await client.GetAsync(dmUrl);
                        if (dmResponse.IsSuccessStatusCode)
                        {
                            string dmJson = await dmResponse.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(dmJson) && dmJson != "null")
                            {
                                var dm = JsonSerializer.Deserialize<DirectMessage>(dmJson);
                                if (dm != null && !string.IsNullOrEmpty(dm.message))
                                {
                                    if (_notifyIcon != null)
                                    {
                                        _notifyIcon.ShowBalloonTip(5000, "AlpNet Pulse - Özel Mesaj", dm.message, WinForms.ToolTipIcon.Info);
                                    }
                                }
                            }
                        }
                    }

                    // 3. Wi-Fi Donanım ve Açık/Kapalı Durumu Kontrolü (Her 3 saniyede bir)
                    CheckAndUpdateWifiVisibility();
                }
            }
            catch { }
        }

        private void ShowInAppMessage(string msg)
        {
            TxtInAppMessage.Text = msg;
            InAppMessageOverlay.Visibility = Visibility.Visible;
        }

        private void CloseMessageBanner_Click(object sender, RoutedEventArgs e)
        {
            InAppMessageOverlay.Visibility = Visibility.Collapsed;
        }

        private async void BtnRestartForUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_updateUrl) && !string.IsNullOrEmpty(_remoteVersion))
            {
                await PerformUpdateAndRestartAsync(_updateUrl, _remoteVersion);
            }
        }

        private bool _isUpdating = false;

        private async Task PerformUpdateAndRestartAsync(string updateUrl, string remoteVersion)
        {
            if (_isUpdating) return;
            _isUpdating = true;

            try
            {
                ShowInAppMessage($"🔄 Yeni sürüm (v{remoteVersion}) bulundu! İndiriliyor ve uygulama yenileniyor, lütfen bekleyin...");

                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentExePath)) return;

                string tempUpdateExe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AlpNet_Update_{Guid.NewGuid():N}.exe");

                using (var response = await _httpClient.GetAsync(updateUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fs = new System.IO.FileStream(tempUpdateExe, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                    {
                        await stream.CopyToAsync(fs);
                    }
                }

                var fileInfo = new System.IO.FileInfo(tempUpdateExe);
                if (!fileInfo.Exists || fileInfo.Length < 500000)
                {
                    _versionBannerShown = false;
                    return;
                }

                // Anti-Loop Koruması: İndirilen dosyanın sürümünü kontrol et
                try
                {
                    var downloadedVerInfo = FileVersionInfo.GetVersionInfo(tempUpdateExe);
                    if (!string.IsNullOrEmpty(downloadedVerInfo.FileVersion))
                    {
                        string dlVerStr = CleanVersion(downloadedVerInfo.FileVersion);
                        string curVerStr = CleanVersion(BASE_VERSION);
                        if (Version.TryParse(dlVerStr, out Version? dlVer) && Version.TryParse(curVerStr, out Version? curVer))
                        {
                            if (dlVer <= curVer)
                            {
                                // İndirilen dosya mevcut sürümden daha yeni değilse döngüye girmemek için güncellemeyi durdur
                                _versionBannerShown = false;
                                try { System.IO.File.Delete(tempUpdateExe); } catch { }
                                return;
                            }
                        }
                    }
                }
                catch { }

                string batchPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"alpnet_update_{Guid.NewGuid():N}.bat");
                string[] rawArgs = Environment.GetCommandLineArgs();
                string passArgs = string.Join(" ", rawArgs.Skip(1).Select(a => $"\"{a}\""));

                string batchContent = $@"@echo off
timeout /t 2 /nobreak > nul
:retry
copy /y ""{tempUpdateExe}"" ""{currentExePath}""
if errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto retry
)
del ""{tempUpdateExe}"" 2>nul
start """" ""{currentExePath}"" {passArgs}
del ""%~f0"" 2>nul
";

                System.IO.File.WriteAllText(batchPath, batchContent, System.Text.Encoding.Default);

                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{batchPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                Process.Start(psi);

                Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });
            }
            catch (Exception ex)
            {
                _versionBannerShown = false;
                Debug.WriteLine("Update error: " + ex.Message);
                ShowInAppMessage($"⚠️ Güncelleme indirilemedi: {ex.Message}");
            }
            finally
            {
                _isUpdating = false;
            }
        }


        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                BtnMaximize.Content = "\uE922";
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMaximize.Content = "\uE923";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Instead of shutting down, just hide the window to the system tray.
            StopWifiAutoScanAndRadarAnimation();
            ShowInTaskbar = false;
            Hide();
        }

        private void CopyDeviceId_Click(object sender, MouseButtonEventArgs e)
        {
            System.Windows.Clipboard.SetText(_deviceId);
            System.Windows.MessageBox.Show("Cihaz Kimliği (Hardware ID) panoya kopyalandı!", "AlpNet Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Menu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn)
            {
                // Close LibreHardwareMonitor when leaving Game Mode page to save RAM and CPU
                if (PageGameMode.Visibility == Visibility.Visible && btn.Name != "MenuBtnGameMode")
                {
                    try
                    {
                        if (_computer != null)
                        {
                            _computer.Close();
                            _computer = null!;
                        }
                    }
                    catch { }
                }

                // Reset all menus
                MenuBtnDashboard.Background = System.Windows.Media.Brushes.Transparent;
                MenuBtnDashboard.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189)); // #ADB5BD
                
                MenuBtnTools.Background = System.Windows.Media.Brushes.Transparent;
                MenuBtnTools.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                
                MenuBtnLicense.Background = System.Windows.Media.Brushes.Transparent;
                MenuBtnLicense.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                MenuBtnWifi.Background = System.Windows.Media.Brushes.Transparent;
                MenuBtnWifi.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                MenuBtnGameMode.Background = System.Windows.Media.Brushes.Transparent;
                MenuBtnGameMode.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                MenuBtnReplay.Background = System.Windows.Media.Brushes.Transparent;
                MenuBtnReplay.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                MenuBtnVoiceTranslation.Background = System.Windows.Media.Brushes.Transparent;
                MenuBtnVoiceTranslation.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                // Hide all pages
                PageDashboard.Visibility = Visibility.Collapsed;
                PageTools.Visibility = Visibility.Collapsed;
                PageWifi.Visibility = Visibility.Collapsed;
                PageGameMode.Visibility = Visibility.Collapsed;
                PageReplay.Visibility = Visibility.Collapsed;
                PageVoiceTranslation.Visibility = Visibility.Collapsed;
                PageLicense.Visibility = Visibility.Collapsed;
                WifiWarningBanner.Visibility = Visibility.Collapsed; // Hide by default on page switch

                // Set Active Menu & Show Page
                btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53)); // #1D2A35
                btn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254)); // #00F2FE

                if (btn.Name == "MenuBtnDashboard")
                {
                    PageDashboard.Visibility = Visibility.Visible;
                    if (LblConnectionType.Text.Contains("Wi-Fi")) WifiWarningBanner.Visibility = Visibility.Visible; // Show only on dashboard if on Wi-Fi
                    PageTitle.Text = "Gösterge Paneli";
                    PageSubtitle.Text = "Gelişmiş Ethernet Aktarım Gücü & Sağlık Analizörü";
                }
                else if (btn.Name == "MenuBtnTools")
                {
                    PageTools.Visibility = Visibility.Visible;
                    PageTitle.Text = "Ağ Araçları";
                    PageSubtitle.Text = "İnternet filtrelerini aşma ve Bağlantı onarım Araçları";
                }
                else if (btn.Name == "MenuBtnWifi")
                {
                    PageWifi.Visibility = Visibility.Visible;
                    PageTitle.Text = "Wi-Fi Canlı Radar & Kanal Analizörü";
                    PageSubtitle.Text = "Çevresel frekans taraması, kanal çakışmaları ve sinyal seviyeleri";
                    StartWifiAutoScanAndRadarAnimation();
                }
                else if (btn.Name == "MenuBtnGameMode")
                {
                    try
                    {
                        if (_computer == null)
                        {
                            _computer = new Computer
                            {
                                IsCpuEnabled = true,
                                IsGpuEnabled = true
                            };
                            _computer.Open();
                        }
                    }
                    catch { }

                    PageGameMode.Visibility = Visibility.Visible;
                    PageTitle.Text = "Oyun Modu & Optimizasyon";
                    PageSubtitle.Text = "Anlık donanım analizi ve oyunlara özel işlemci ÇEKİRDEK nceliklendirme SİSTEMi";
                    RefreshGamesList_Click(null, null); // Otomatik liste yenileme
                }
                else if (btn.Name == "MenuBtnReplay")
                {
                    PageReplay.Visibility = Visibility.Visible;
                    PageTitle.Text = "Oyun Kaydı & Anlık Tekrar";
                    PageSubtitle.Text = "Arka planda düşük performans tüketimiyle anlık tekrar alma sistemi";
                }
                else if (btn.Name == "MenuBtnVoiceTranslation")
                {
                    PageVoiceTranslation.Visibility = Visibility.Visible;
                    PageTitle.Text = "Sesli Çeviri";
                    PageSubtitle.Text = "Anlık sesli iletişim çeviri sistemi";
                    LoadVoiceDevices();
                }
                else if (btn.Name == "MenuBtnLicense")
                {
                    PageLicense.Visibility = Visibility.Visible;
                    PageTitle.Text = "Lisans & Hesap";
                    PageSubtitle.Text = "Yazılım lisans DURUMU ve ciHızıbilgileri";
                }
            }
        }


        #region Wi-Fi Analyzer & Rotating Radar
        private List<WifiNetwork> _lastScannedNetworks = new List<WifiNetwork>();
        private System.Windows.Threading.DispatcherTimer? _wifiRadarTimer;
        private System.Windows.Threading.DispatcherTimer? _wifiAutoScanTimer;
        private double _radarSweepAngle = 0;
        private System.Windows.Shapes.Line? _sweepLineElement;
        private List<System.Windows.Shapes.Polygon> _sweepTrailSlices = new List<System.Windows.Shapes.Polygon>();

        private class RadarBlip
        {
            public double AngleDegrees { get; set; }
            public System.Windows.Shapes.Ellipse Dot { get; set; } = null!;
            public System.Windows.Shapes.Ellipse GlowRing { get; set; } = null!;
            public System.Windows.Controls.TextBlock Label { get; set; } = null!;
            public bool IsConnected { get; set; }
        }
        private List<RadarBlip> _radarBlips = new List<RadarBlip>();

        public class WifiNetwork
        {
            public string Ssid { get; set; } = "";
            public string Signal { get; set; } = "";
            public string ChannelInfo { get; set; } = "";
            public string Band { get; set; } = "";
            public string SignalColor { get; set; } = "#FFFFFF";
            public string ChannelColor { get; set; } = "#ADB5BD";
            public bool IsConnected { get; set; } = false;
            public string ItemBackground { get; set; } = "#11131A";
            public string ItemBorder { get; set; } = "#252833";
            public System.Windows.Visibility ConnectedVisibility { get; set; } = System.Windows.Visibility.Collapsed;
        }

        private void StartWifiAutoScanAndRadarAnimation()
        {
            RebuildRadarCanvas();

            if (_wifiRadarTimer == null)
            {
                _wifiRadarTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(25)
                };
                _wifiRadarTimer.Tick += (s, e) =>
                {
                    _radarSweepAngle = (_radarSweepAngle + 4) % 360;
                    UpdateRadarSweep();
                };
            }
            _wifiRadarTimer.Start();

            if (_wifiAutoScanTimer == null)
            {
                _wifiAutoScanTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(6)
                };
                _wifiAutoScanTimer.Tick += (s, e) => PerformWifiScanAsync();
            }
            _wifiAutoScanTimer.Start();

            PerformWifiScanAsync();
        }

        private void StopWifiAutoScanAndRadarAnimation()
        {
            _wifiRadarTimer?.Stop();
            _wifiAutoScanTimer?.Stop();
        }

        private bool _isScanningWifi = false;

        private async void PerformWifiScanAsync()
        {
            if (_isScanningWifi) return;
            if (PageWifi.Visibility != Visibility.Visible || WindowState == WindowState.Minimized || !IsVisible || !ShowInTaskbar)
            {
                StopWifiAutoScanAndRadarAnimation();
                return;
            }
            _isScanningWifi = true;

            Dispatcher.Invoke(() => WifiScanStatus.Text = "📡 Çevredeki Wi-Fi ağları otomatik taranıyor...");

            var networks = new List<WifiNetwork>();
            string connectedSsid = "";

            await Task.Run(() =>
            {
                // Detect currently connected Wi-Fi SSID
                try
                {
                    var psiInt = new ProcessStartInfo("netsh", "wlan show interfaces")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };
                    using var procInt = Process.Start(psiInt);
                    if (procInt != null)
                    {
                        string intOut = procInt.StandardOutput.ReadToEnd();
                        foreach (var l in intOut.Split('\n'))
                        {
                            string line = l.Trim();
                            if (line.StartsWith("SSID") && !line.StartsWith("BSSID"))
                            {
                                var parts = line.Split(':');
                                if (parts.Length > 1)
                                {
                                    connectedSsid = parts[1].Trim();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    NativeWifi.TriggerScan();
                    System.Threading.Thread.Sleep(2000);
                    
                    var psi = new ProcessStartInfo("netsh", "wlan show networks mode=bssid")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        string currentSsid = "Bilinmiyor";
                        
                        var lines = output.Split('\n');
                        WifiNetwork? currentNetwork = null;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i].Trim();
                            if (line.StartsWith("SSID"))
                            {
                                var parts = line.Split(':');
                                if (parts.Length > 1) currentSsid = parts[1].Trim();
                                if (string.IsNullOrEmpty(currentSsid)) currentSsid = "Gizli Ağ";
                            }
                            else if (line.StartsWith("BSSID") || line.StartsWith("Sinyal") || line.StartsWith("Signal") || line.StartsWith("Kanal") || line.StartsWith("Channel") || line.StartsWith("Bant") || line.StartsWith("Band"))
                            {
                                if (line.Contains("Utilization") || line.Contains("Kullanım")) continue;

                                if (line.StartsWith("BSSID"))
                                {
                                    if (currentNetwork != null) networks.Add(currentNetwork);
                                    currentNetwork = new WifiNetwork { Ssid = currentSsid };
                                }
                                else if (currentNetwork != null)
                                {
                                    var parts = line.Split(':');
                                    if (parts.Length > 1)
                                    {
                                        string val = parts[1].Trim();
                                        if (line.StartsWith("Sinyal") || line.StartsWith("Signal"))
                                        {
                                            currentNetwork.Signal = val;
                                            if (val.Contains("%"))
                                            {
                                                if (int.TryParse(val.Replace("%", ""), out int sigVal))
                                                {
                                                    if (sigVal >= 80) currentNetwork.SignalColor = "#00FF87";
                                                    else if (sigVal >= 50) currentNetwork.SignalColor = "#FFCC00";
                                                    else currentNetwork.SignalColor = "#FF0844";
                                                }
                                            }
                                        }
                                        else if (line.StartsWith("Kanal") || line.StartsWith("Channel"))
                                        {
                                            currentNetwork.ChannelInfo = "Kanal " + val;
                                        }
                                        else if (line.StartsWith("Bant") || line.StartsWith("Band"))
                                        {
                                            currentNetwork.Band = val;
                                        }
                                    }
                                }
                            }
                        }
                        if (currentNetwork != null) networks.Add(currentNetwork);
                    }
                }
                catch { }
            });

            // Group and analyze collision
            var grouped = networks.GroupBy(n => n.ChannelInfo).ToList();
            foreach (var net in networks)
            {
                if (!string.IsNullOrEmpty(connectedSsid) && net.Ssid.Equals(connectedSsid, StringComparison.OrdinalIgnoreCase))
                {
                    net.IsConnected = true;
                    net.ItemBackground = "#11291F";
                    net.ItemBorder = "#00FF87";
                    net.ConnectedVisibility = Visibility.Visible;
                }

                if (!string.IsNullOrEmpty(net.Band))
                {
                    net.ChannelInfo += $" ({net.Band})";
                }
                
                int count = grouped.FirstOrDefault(g => g.Key == net.ChannelInfo)?.Count() ?? 1;
                if (count > 3) 
                {
                    net.ChannelInfo += " (Çakışma Var!)";
                    net.ChannelColor = "#FF0844";
                }
                else if (count > 1)
                {
                    net.ChannelColor = "#FFCC00";
                }
                else
                {
                    net.ChannelInfo += " (Temiz Kanal)";
                    net.ChannelColor = "#00FF87";
                }
            }

            _lastScannedNetworks = networks.OrderByDescending(n => n.IsConnected)
                                           .ThenByDescending(n => n.Signal)
                                           .ToList();
            WifiNetworksList.ItemsSource = _lastScannedNetworks;

            if (_lastScannedNetworks.Count == 0)
            {
                WifiScanStatus.Text = "⚠️ Çevrede hiçbir Wi-Fi ağı bulunamadı veya Wi-Fi kapalı.";
                _ = Task.Run(() => CheckAndUpdateWifiVisibility());
            }
            else
            {
                WifiScanStatus.Text = $"🟢 Canlı Tarama: {_lastScannedNetworks.Count} farklı Wi-Fi ağı saptandı.";
            }

            RebuildRadarCanvas();
            _isScanningWifi = false;
        }

        private void WifiRadarCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RebuildRadarCanvas();
        }

        private void RebuildRadarCanvas()
        {
            WifiRadarCanvas.Children.Clear();
            _sweepTrailSlices.Clear();
            _radarBlips.Clear();

            double width = WifiRadarCanvas.ActualWidth;
            double height = WifiRadarCanvas.ActualHeight;

            if (width <= 0 || height <= 0) return;

            double centerX = width / 2;
            double centerY = height / 2;
            double maxRadius = Math.Min(centerX, centerY) - 15;

            // 1. Concentric radar grid circles
            for (int i = 1; i <= 4; i++)
            {
                var circle = new System.Windows.Shapes.Ellipse
                {
                    Width = maxRadius * 2 * (i / 4.0),
                    Height = maxRadius * 2 * (i / 4.0),
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0, 242, 254)),
                    StrokeThickness = 1,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(6, 0, 242, 254))
                };
                System.Windows.Controls.Canvas.SetLeft(circle, centerX - circle.Width / 2);
                System.Windows.Controls.Canvas.SetTop(circle, centerY - circle.Height / 2);
                WifiRadarCanvas.Children.Add(circle);
            }

            // 2. Radar crosshair axes (4 directions)
            double[] angles = { 0, 45, 90, 135 };
            foreach (double a in angles)
            {
                double radA = a * Math.PI / 180.0;
                var axisLine = new System.Windows.Shapes.Line
                {
                    X1 = centerX - maxRadius * Math.Cos(radA),
                    Y1 = centerY - maxRadius * Math.Sin(radA),
                    X2 = centerX + maxRadius * Math.Cos(radA),
                    Y2 = centerY + maxRadius * Math.Sin(radA),
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(25, 0, 242, 254)),
                    StrokeThickness = 1
                };
                WifiRadarCanvas.Children.Add(axisLine);
            }

            // 3. Layered Fading Phosphor Trail Slices behind sweep beam
            int sliceCount = 5;
            byte[] sliceAlphas = { 110, 70, 40, 20, 8 };
            for (int i = 0; i < sliceCount; i++)
            {
                var slice = new System.Windows.Shapes.Polygon
                {
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(sliceAlphas[i], 0, 255, 135))
                };
                WifiRadarCanvas.Children.Add(slice);
                _sweepTrailSlices.Add(slice);
            }

            // 4. Scanner Beam Line
            _sweepLineElement = new System.Windows.Shapes.Line
            {
                X1 = centerX,
                Y1 = centerY,
                X2 = centerX + maxRadius,
                Y2 = centerY,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135)),
                StrokeThickness = 2.5
            };
            WifiRadarCanvas.Children.Add(_sweepLineElement);

            // 5. Center Point (Local Wi-Fi Adapter)
            var centerDot = new System.Windows.Shapes.Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135)),
                ToolTip = "Sizin Wi-Fi Konumunuz"
            };
            System.Windows.Controls.Canvas.SetLeft(centerDot, centerX - 6);
            System.Windows.Controls.Canvas.SetTop(centerDot, centerY - 6);
            WifiRadarCanvas.Children.Add(centerDot);

            // 6. Plot detected Wi-Fi target blips with phosphor decay capability
            foreach (var net in _lastScannedNetworks)
            {
                string sigStr = net.Signal.Replace("%", "").Trim();
                if (int.TryParse(sigStr, out int sigVal))
                {
                    double distance = maxRadius * (1.0 - (sigVal / 100.0) * 0.85);
                    if (distance < 25) distance = 25;

                    double angleDeg = (Math.Abs(net.Ssid.GetHashCode()) % 360);
                    double angleRad = angleDeg * (Math.PI / 180.0);

                    double x = centerX + distance * Math.Cos(angleRad);
                    double y = centerY + distance * Math.Sin(angleRad);

                    var baseColor = net.IsConnected ? System.Windows.Media.Color.FromRgb(0, 255, 135) :
                                   (net.ChannelColor == "#FF0844" ? System.Windows.Media.Color.FromRgb(255, 8, 68) :
                                   (net.ChannelColor == "#FFCC00" ? System.Windows.Media.Color.FromRgb(255, 204, 0) : System.Windows.Media.Color.FromRgb(0, 242, 254)));

                    // Outer pulse glow ring
                    var glowRing = new System.Windows.Shapes.Ellipse
                    {
                        Width = net.IsConnected ? 26 : 20,
                        Height = net.IsConnected ? 26 : 20,
                        Stroke = new SolidColorBrush(baseColor),
                        StrokeThickness = net.IsConnected ? 2.0 : 1.5,
                        Opacity = net.IsConnected ? 0.9 : 0.0,
                        IsHitTestVisible = false
                    };
                    System.Windows.Controls.Canvas.SetLeft(glowRing, x - (net.IsConnected ? 13 : 10));
                    System.Windows.Controls.Canvas.SetTop(glowRing, y - (net.IsConnected ? 13 : 10));
                    WifiRadarCanvas.Children.Add(glowRing);

                    // Solid Target Dot
                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = net.IsConnected ? 14 : 10,
                        Height = net.IsConnected ? 14 : 10,
                        Fill = new SolidColorBrush(baseColor),
                        Opacity = net.IsConnected ? 1.0 : 0.15,
                        ToolTip = net.IsConnected ? $"🟢 [BAĞLI AĞ] {net.Ssid} (Sinyal: %{sigVal}, {net.ChannelInfo})" : $"{net.Ssid} (Sinyal: %{sigVal}, {net.ChannelInfo})"
                    };
                    System.Windows.Controls.Canvas.SetLeft(dot, x - (net.IsConnected ? 7 : 5));
                    System.Windows.Controls.Canvas.SetTop(dot, y - (net.IsConnected ? 7 : 5));
                    WifiRadarCanvas.Children.Add(dot);

                    // Network SSID Label
                    var label = new System.Windows.Controls.TextBlock
                    {
                        Text = net.IsConnected ? $"★ {net.Ssid} (Bağlı)" : net.Ssid,
                        Foreground = new SolidColorBrush(baseColor),
                        FontSize = net.IsConnected ? 11 : 10,
                        FontWeight = FontWeights.Bold,
                        Opacity = net.IsConnected ? 1.0 : 0.15,
                        IsHitTestVisible = false
                    };
                    System.Windows.Controls.Canvas.SetLeft(label, x + (net.IsConnected ? 10 : 8));
                    System.Windows.Controls.Canvas.SetTop(label, y - 7);
                    WifiRadarCanvas.Children.Add(label);

                    _radarBlips.Add(new RadarBlip
                    {
                        AngleDegrees = angleDeg,
                        Dot = dot,
                        GlowRing = glowRing,
                        Label = label,
                        IsConnected = net.IsConnected
                    });
                }
            }

            UpdateRadarSweep();
        }

        private void UpdateRadarSweep()
        {
            if (_sweepLineElement == null || WifiRadarCanvas.ActualWidth <= 0) return;

            double width = WifiRadarCanvas.ActualWidth;
            double height = WifiRadarCanvas.ActualHeight;
            double centerX = width / 2;
            double centerY = height / 2;
            double maxRadius = Math.Min(centerX, centerY) - 15;

            // 1. Update Scanner Beam Line
            double mainRad = _radarSweepAngle * Math.PI / 180.0;
            double endX = centerX + maxRadius * Math.Cos(mainRad);
            double endY = centerY + maxRadius * Math.Sin(mainRad);

            _sweepLineElement.X1 = centerX;
            _sweepLineElement.Y1 = centerY;
            _sweepLineElement.X2 = endX;
            _sweepLineElement.Y2 = endY;

            // 2. Update Fading Phosphor Trail Slices behind sweep line
            int sliceCount = _sweepTrailSlices.Count;
            double totalTrailAngle = 50.0;
            double sliceSpan = totalTrailAngle / sliceCount;

            for (int i = 0; i < sliceCount; i++)
            {
                double startA = (_radarSweepAngle - (i * sliceSpan)) * Math.PI / 180.0;
                double endA = (_radarSweepAngle - ((i + 1) * sliceSpan)) * Math.PI / 180.0;

                double p1X = centerX + maxRadius * Math.Cos(startA);
                double p1Y = centerY + maxRadius * Math.Sin(startA);
                double p2X = centerX + maxRadius * Math.Cos(endA);
                double p2Y = centerY + maxRadius * Math.Sin(endA);

                _sweepTrailSlices[i].Points = new PointCollection
                {
                    new System.Windows.Point(centerX, centerY),
                    new System.Windows.Point(p1X, p1Y),
                    new System.Windows.Point(p2X, p2Y)
                };
            }

            // 3. Update Blip Hit Flash & Phosphor Decay (Real Radar Beam Intercept)
            foreach (var blip in _radarBlips)
            {
                double diff = (_radarSweepAngle - blip.AngleDegrees + 360) % 360;

                if (diff <= 10)
                {
                    // BEAM INTERCEPT! Instant Flash!
                    blip.Dot.Opacity = 1.0;
                    blip.GlowRing.Opacity = 0.9;
                    blip.Label.Opacity = 1.0;
                }
                else if (diff <= 270)
                {
                    // Realistic quadratic phosphor decay / fading out after sweep line passes
                    double norm = 1.0 - (diff / 270.0);
                    double decay = norm * norm; // quadratic decay

                    double dotOpacity = blip.IsConnected ? (0.6 + 0.4 * decay) : (0.15 + 0.85 * decay);
                    blip.Dot.Opacity = dotOpacity;
                    blip.GlowRing.Opacity = blip.IsConnected ? (0.5 * decay + 0.2) : (0.7 * decay);
                    blip.Label.Opacity = blip.IsConnected ? 1.0 : Math.Max(0.25, dotOpacity);
                }
                else
                {
                    // Dimmest state waiting for next radar sweep pass
                    blip.Dot.Opacity = blip.IsConnected ? 0.6 : 0.15;
                    blip.GlowRing.Opacity = blip.IsConnected ? 0.2 : 0.0;
                    blip.Label.Opacity = blip.IsConnected ? 0.9 : 0.2;
                }
            }
        }
        #endregion
        #endregion

        #region Licensing Logic
        private async Task InitDeviceId()
        {
            await Task.Run(() =>
            {
                // 1. Registry MachineGuid
                try
                {
                    using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    {
                        if (key != null)
                        {
                            var guid = key.GetValue("MachineGuid")?.ToString();
                            if (!string.IsNullOrEmpty(guid))
                            {
                                _deviceId = Md5Hash(guid.Trim());
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("MachineGuid read error: " + ex.Message);
                }

                // 2. WMI Motherboard BIOS UUID
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            string uuid = obj["UUID"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(uuid) && uuid != "00000000-0000-0000-0000-000000000000" && uuid != "-")
                            {
                                _deviceId = Md5Hash(uuid.Trim());
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("BIOS UUID read error: " + ex.Message);
                }

                // 3. Fallback MAC Address + Hostname
                try
                {
                    var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                    var macs = new List<string>();
                    foreach (var ni in interfaces)
                    {
                        if (ni.NetworkInterfaceType != NetworkInterfaceType.Loopback && ni.OperationalStatus == OperationalStatus.Up)
                        {
                            var mac = ni.GetPhysicalAddress().ToString();
                            if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                            {
                                macs.Add(mac);
                            }
                        }
                    }
                    if (macs.Count > 0)
                    {
                        macs.Sort();
                        string combined = string.Join("-", macs) + "-" + Environment.MachineName;
                        _deviceId = Md5Hash(combined);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("MAC fallback error: " + ex.Message);
                }

                // 4. Ultimate Fallback
                _deviceId = Md5Hash(Environment.MachineName + Environment.UserName);
            });
        }

        private string Md5Hash(string input)
        {
            using (var md5 = MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(bytes).Replace("-", "").ToUpper();
            }
        }

        private async Task LoadLicenseConfig()
        {
            if (File.Exists(_licenseFile))
            {
                try
                {
                    string json = File.ReadAllText(_licenseFile);
                    var data = JsonSerializer.Deserialize<LicenseState>(json);
                    if (data != null)
                    {
                        _licenseState.LicenseKey = data.LicenseKey ?? "";
                        _licenseState.ExpiresAt = data.ExpiresAt ?? "";
                        _licenseState.DeviceId = data.DeviceId ?? "";

                        TxtLicenseKey.Text = _licenseState.LicenseKey;

                        if (await VerifyOfflineLicense(_licenseState.LicenseKey))
                        {
                            _licenseState.Licensed = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("License config parsing error: " + ex.Message);
                }
            }
            else
            {
                SaveLicenseConfig();
            }
        }

        private void SaveLicenseConfig()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(new LicenseState
                {
                    LicenseKey = _licenseState.LicenseKey,
                    ExpiresAt = _licenseState.ExpiresAt,
                    DeviceId = _deviceId
                }, options);
                File.WriteAllText(_licenseFile, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("License write error: " + ex.Message);
            }
        }

        private async void CheckLicenseAndVerify()
        {
            // 1. AlpTogether Ekosistem Bulut Yetkisi (Modern Whitelist Doğrulaması)
            try
            {
                if (await CheckAlpTogetherCloudWhitelistAsync())
                {
                    _licenseState.Licensed = true;
                    SaveLicenseConfig();
                    ShowDashboard();
                    return;
                }
            }
            catch { }

            // 2. Çevrimdışı / Mevcut Lisans Anahtarı Doğrulaması (Eski kullanıcıların kopmaması için tam koruma)
            if (!string.IsNullOrEmpty(_licenseState.LicenseKey))
            {
                if (await VerifyOfflineLicense(_licenseState.LicenseKey))
                {
                    _licenseState.Licensed = true;
                    ShowDashboard();
                    return;
                }
            }

            _licenseState.Licensed = false;
            ShowLicenseOverlay();
        }

        private async Task<bool> CheckAlpTogetherCloudWhitelistAsync()
        {
            if (string.IsNullOrEmpty(_deviceId)) return false;

            try
            {
                string firebaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/alptogether/users.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var resp = await _httpClient.GetAsync(firebaseUrl, cts.Token);
                if (!resp.IsSuccessStatusCode) return false;

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return false;

                var users = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (users == null) return false;

                foreach (var kvp in users)
                {
                    var user = kvp.Value;
                    bool match = false;

                    // AlpNet HWID eşleşmesi veya AlpTogether HWID eşleşmesi
                    if (user.TryGetProperty("alpnetHwid", out var anHwidProp) &&
                        string.Equals(anHwidProp.GetString()?.Trim(), _deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                    }
                    else if (user.TryGetProperty("hwid", out var hwidProp) &&
                             string.Equals(hwidProp.GetString()?.Trim(), _deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                    }
                    else if (string.Equals(kvp.Key.Trim(), _deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                    }

                    if (match)
                    {
                        if (user.TryGetProperty("appPermissions", out var perms) &&
                            perms.TryGetProperty("alpnet-pulse", out var pulsePerm))
                        {
                            bool hasAccess = pulsePerm.TryGetProperty("hasAccess", out var acc) && acc.GetBoolean();
                            if (hasAccess)
                            {
                                string expiresAt = pulsePerm.TryGetProperty("expiresAt", out var exp) ? exp.GetString() ?? "" : "";
                                int durationDays = pulsePerm.TryGetProperty("durationDays", out var dur) ? dur.GetInt32() : 0;

                                if (durationDays >= 9999 || expiresAt.StartsWith("9999"))
                                {
                                    _licenseState.ExpiresAt = "Süresiz (AlpTogether)";
                                    _licenseState.LicenseKey = "ALPTOGETHER-WHITELIST-" + _deviceId;
                                    return true;
                                }

                                if (DateTime.TryParse(expiresAt, out DateTime expDate))
                                {
                                    if (DateTime.Now <= expDate)
                                    {
                                        _licenseState.ExpiresAt = expDate.ToString("dd.MM.yyyy HH:mm");
                                        _licenseState.LicenseKey = "ALPTOGETHER-WHITELIST-" + _deviceId;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private async void CheckAlpTogether_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn)
            {
                btn.IsEnabled = false;
                btn.Content = "Sorgulanıyor...";
            }

            bool ok = await CheckAlpTogetherCloudWhitelistAsync();
            if (ok)
            {
                _licenseState.Licensed = true;
                SaveLicenseConfig();
                ShowDashboard();
            }
            else
            {
                ShowLicenseError("AlpTogether bulutunda bu cihaz için aktif bir AlpNet Pulse yetkisi bulunamadı. Lütfen yönetici (Alphan) ile iletişime geçiniz.");
            }

            if (sender is System.Windows.Controls.Button btnRestore)
            {
                btnRestore.IsEnabled = true;
                btnRestore.Content = "✦ AlpTogether Yetkisini Doğrula 🔄";
            }
        }

        private async Task<bool> VerifyOfflineLicense(string keyStr)
        {
            if (string.IsNullOrEmpty(keyStr)) return false;

            // Check Blacklist remotely
            try
            {
                string blacklistUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/blacklist.json";
                foreach (var client in new[] { _httpClient })
                {
                    var response = await client.GetAsync(blacklistUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var blacklist = JsonSerializer.Deserialize<List<string>>(json);
                            if (blacklist != null && (blacklist.Contains(_deviceId) || blacklist.Contains(keyStr)))
                            {
                                return false; // Key or Device is revoked
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore if offline or url is invalid 
            }

            // TRIAL LOGIC
            if (keyStr == "TRIAL-" + _deviceId)
            {
                try
                {
                    string trialUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/trials/{_deviceId}.json";
                    foreach (var client in new[] { _httpClient })
                    {
                        var response = await client.GetAsync(trialUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            string json = await response.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(json) && json != "null")
                            {
                                json = json.Replace("\"", "");
                                if (DateTime.TryParse(json, out DateTime expireDate))
                                {
                                    if (DateTime.Now < expireDate)
                                    {
                                        _licenseState.ExpiresAt = expireDate.ToString("dd.MM.yyyy HH:mm");
                                        return true; // Trial active
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
                return false; // Trial expired or invalid
            }

            try
            {
                string rawKey = keyStr.Replace("-", "").Replace("ALPNETPULSE", "");
                byte[] decodedBytes = Convert.FromBase64String(rawKey);
                string fullKeyData = Encoding.UTF8.GetString(decodedBytes);

                string[] parts = fullKeyData.Split('|');
                if (parts.Length != 3) return false;

                string targetDeviceId = parts[0];
                string expTicksStr = parts[1];
                string signature = parts[2];

                if (targetDeviceId != _deviceId) return false;

                string payload = $"{targetDeviceId}|{expTicksStr}";
                string expectedSig = "";
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("AlpNetSecret2026!PulseCore")))
                {
                    byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                    expectedSig = Convert.ToBase64String(hash);
                }

                if (signature != expectedSig) return false;

                if (long.TryParse(expTicksStr, out long ticks))
                {
                    DateTime expDate = new DateTime(ticks);
                    _licenseState.ExpiresAt = expDate.ToString("o");
                    if (DateTime.Now > expDate)
                    {
                        return false; // Expired
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private void ShowLicenseOverlay()
        {
            LicenseOverlay.Visibility = Visibility.Visible;
            MainDashboard.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void ShowDashboard()
        {
            LicenseOverlay.Visibility = Visibility.Collapsed;
            MainDashboard.Visibility = Visibility.Visible;
            LoadingOverlay.Visibility = Visibility.Collapsed;

            string expStr = "Süresiz";
            if (!string.IsNullOrEmpty(_licenseState.ExpiresAt))
            {
                if (DateTime.TryParse(_licenseState.ExpiresAt, out DateTime exp))
                {
                    expStr = exp.ToString("dd.MM.yyyy");
                }
            }
            PageLicenseStatus.Text = "Lisans Aktif";
            PageLicenseStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
            PageLicenseExpiry.Text = $"Bitiş Tarihi: {expStr}";

            UpdateNetworkStats();
            _ = FetchGeoIpAndNetworkDetails();
        }

        private async void ActivateLicense_Click(object? sender, RoutedEventArgs? e)
        {
            string key = TxtLicenseKey.Text.Trim();

            if (string.IsNullOrEmpty(key))
            {
                ShowLicenseError("Lütfen lisans anahtarını girin.");
                return;
            }

            LicenseErrorBorder.Visibility = Visibility.Collapsed;
            BtnActivate.IsEnabled = false;
            BtnActivate.Content = "LİSANSI AKTİFLEŞTİR ➔";

            if (await VerifyOfflineLicense(key))
            {
                _licenseState.Licensed = true;
                _licenseState.LicenseKey = key;
                SaveLicenseConfig();
                PresenceTimer_Tick(null, null);
                ShowDashboard();
            }
            else
            {
                ShowLicenseError("Lütfen lisans anahtarını girin.");
            }

            BtnActivate.IsEnabled = true;
            BtnActivate.Content = "LİSANSI AKTİFLEŞTİR ➔";
        }

        private void ShowLicenseError(string message)
        {
            LblLicenseError.Text = message;
            LicenseErrorBorder.Visibility = Visibility.Visible;
        }

        private async void StartTrial_Click(object sender, RoutedEventArgs e)
        {
            BtnTrial.IsEnabled = false;
            BtnTrial.Content = "BEKLEYİN...";
            LicenseErrorBorder.Visibility = Visibility.Collapsed;

            try
            {
                string firebaseUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/trials/{_deviceId}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                
                foreach (var client in new[] { _httpClient })
                {
                    // Check if trial already exists
                    var getRes = await client.GetAsync(firebaseUrl);
                    if (getRes.IsSuccessStatusCode)
                    {
                        string json = await getRes.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            ShowLicenseError("Bu cihaz için daha önce deneme sürümü kullanılmış!");
                            BtnTrial.IsEnabled = true;
                            BtnTrial.Content = "âš¡ 1 Saat Ücretsiz Dene";
                            return;
                        }
                    }

                    // Create new trial (1 hour)
                    DateTime expireTime = DateTime.Now.AddHours(1);
                    string expireStr = expireTime.ToString("o"); // ISO 8601
                    var content = new StringContent($"\"{expireStr}\"", Encoding.UTF8, "application/json");
                    var putRes = await client.PutAsync(firebaseUrl, content);
                    
                    if (putRes.IsSuccessStatusCode)
                    {
                        TxtLicenseKey.Text = "TRIAL-" + _deviceId;
                        ActivateLicense_Click(null, null);
                        return;
                    }
                }
                
                ShowLicenseError("Sunucuya bağlanılamadı, Lütfen internetinizi kontrol edin.");
            }
            catch
            {
                ShowLicenseError("Deneme sürümü başlatılamadı. Bağlantı hatası.");
            }

            BtnTrial.IsEnabled = true;
            BtnTrial.Content = "âš¡ 1 Saat Ücretsiz Dene";
        }

        private void BuyWeekly_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string message = $"Merhaba, AlpNet Pulse Haftalık Lisans satın almak istiyorum. HWID: {_deviceId}";
                string urlEncodedMessage = Uri.EscapeDataString(message);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://wa.me/905059092062?text=" + urlEncodedMessage,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BuyMonthly_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string message = $"Merhaba, AlpNet Pulse Aylık Lisans satın almak istiyorum. HWID: {_deviceId}";
                string urlEncodedMessage = Uri.EscapeDataString(message);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://wa.me/905059092062?text=" + urlEncodedMessage,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void DeactivateLicense_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("Lisans anahtarını bu cihazdan kaldırmak istediğinize emin misiniz?", "Lisans Kaldır", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _licenseState.Licensed = false;
                _licenseState.LicenseKey = "";
                SaveLicenseConfig();
                ShowLicenseOverlay();
            }
        }

        #region AlpTogether Uyumlu Teşhis & Log Gönderimi
        private async void BtnSendDiagnosticLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnSendDiagnosticLogs.IsEnabled = false;
                BtnSendDiagnosticLogs.Content = "⏳ Gönderiliyor...";
                PnlDiagStatus.Visibility = Visibility.Visible;
                TxtLicenseDiagStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
                TxtLicenseDiagStatus.Text = "📡 Sistem, ağ ve WireSock teşhis verileri toplanıyor, AlpTogether merkezine iletiliyor...";

                string note = TxtLicenseDiagNote.Text.Trim();
                string licStatus = PageLicenseStatus.Text;
                string licExp = PageLicenseExpiry.Text;

                var result = await AlpNetTelemetryService.Instance.SendDiagnosticsAndLogsAsync(
                    customDeviceId: _deviceId,
                    licenseStatus: licStatus,
                    licenseExpiry: licExp,
                    userNote: string.IsNullOrWhiteSpace(note) ? "Kullanıcı Lisans & Hesap ekranından sorun teşhisi için log gönderdi." : note);

                if (result.Success)
                {
                    TxtLicenseDiagStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                    TxtLicenseDiagStatus.Text = $"✅ Teşhis loglarınız AlpTogether geliştirici ekibine başarıyla iletildi!\nRapor Takip Kodu: {result.ReportId}";
                    BtnSendDiagnosticLogs.Content = "✔ GÖNDERİLDİ";
                    TxtLicenseDiagNote.Text = "";
                }
                else
                {
                    TxtLicenseDiagStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 171, 0));
                    TxtLicenseDiagStatus.Text = result.Message;
                    BtnSendDiagnosticLogs.Content = "🚀 TEKRAR DENE";
                }
            }
            catch (Exception ex)
            {
                TxtLicenseDiagStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 23, 68));
                TxtLicenseDiagStatus.Text = $"❌ Log gönderim hatası: {ex.Message}";
                BtnSendDiagnosticLogs.Content = "🚀 TEKRAR DENE";
            }
            finally
            {
                await Task.Delay(2500);
                BtnSendDiagnosticLogs.IsEnabled = true;
                if (BtnSendDiagnosticLogs.Content.ToString() == "✔ GÖNDERİLDİ")
                {
                    await Task.Delay(2500);
                    BtnSendDiagnosticLogs.Content = "🚀 TEŞHİS VE SİSTEM LOGLARINI GÖNDER";
                }
            }
        }

        private void BtnOpenLocalLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logDir = AlpNetTelemetryService.Instance.GetLogsDirectory();
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Klasör açılamadı: " + ex.Message, "AlpNet Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnOverlaySendLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnOverlaySendLogs.IsEnabled = false;
                BtnOverlaySendLogs.Content = "⏳ Gönderiliyor...";
                
                var result = await AlpNetTelemetryService.Instance.SendDiagnosticsAndLogsAsync(
                    customDeviceId: _deviceId,
                    licenseStatus: "Aktivasyon Bekleniyor / Kilitli",
                    licenseExpiry: "-",
                    userNote: "Kullanıcı aktivasyon ekranından 'Sorun Bildir / Teşhis Logu Gönder' butonunu kullandı.");

                if (result.Success)
                {
                    System.Windows.MessageBox.Show(
                        $"✅ Teşhis loglarınız AlpTogether geliştirici ekibine iletildi!\nRapor Takip Kodu: {result.ReportId}",
                        "AlpNet Pulse Teşhis",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        $"Loglar yerel olarak kaydedildi, ancak buluta iletilemedi:\n{result.Message}",
                        "AlpNet Pulse",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Hata: " + ex.Message, "AlpNet Pulse", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnOverlaySendLogs.IsEnabled = true;
                BtnOverlaySendLogs.Content = "📡 Sorun mu var? Teşhis Logu Gönder";
            }
        }
        #endregion

        private void LicenseCheckTimer_Tick(object? sender, EventArgs e)
        {
            if (_licenseState.Licensed)
            {
                CheckLicenseAndVerify();
            }
        }

        private async void PresenceTimer_Tick(object? sender, EventArgs? e)
        {
            if (string.IsNullOrEmpty(_deviceId) || !_licenseState.Licensed) return;

            try
            {
                string firebaseUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/users/{_deviceId}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                
                foreach (var client in new[] { _httpClient })
                {
                    // 1. Fetch remote first to check for remote license assignment
                    var getRes = await client.GetAsync(firebaseUrl);
                    if (getRes.IsSuccessStatusCode)
                    {
                        string jsonRes = await getRes.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(jsonRes) && jsonRes != "null")
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonRes);
                            if (dict != null && dict.TryGetValue("license_key", out var remoteKeyElement))
                            {
                                string remoteKey = remoteKeyElement.GetString() ?? "";
                                if (!string.IsNullOrEmpty(remoteKey) && remoteKey != _licenseState.LicenseKey && !remoteKey.StartsWith("TRIAL-"))
                                {
                                    // Verify remote key
                                    if (await VerifyOfflineLicense(remoteKey))
                                    {
                                        _licenseState.LicenseKey = remoteKey;
                                        SaveLicenseConfig();
                                        
                                        // Update UI if on main thread or via Dispatcher
                                        Dispatcher.Invoke(() => {
                                            TxtLicenseKey.Text = remoteKey;
                                            ActivateLicense_Click(null, null);
                                        });
                                    }
                                }
                            }
                        }
                    }

                    // 2. Now push our presence and current state
                    var data = new
                    {
                        hwid = _deviceId,
                        license_key = _licenseState.LicenseKey,
                        last_seen = DateTime.UtcNow.ToString("o"),
                        version = BASE_VERSION,
                        machine_name = Environment.MachineName,
                        pc_user = Environment.UserName,
                        user_name = Environment.UserName,
                        warp_active = (_warpManager != null && _warpManager.IsActive) || WarpManager.IsWireSockServiceRunning(),
                        warp_started_at = _warpManager.WarpStartedAt?.ToString("o") ?? "",
                        warp_stopped_at = _warpManager.WarpStoppedAt?.ToString("o") ?? "",
                        warp_total_hours_today = Math.Round(_warpManager.WarpTotalHoursToday, 2)
                    };

                    string json = JsonSerializer.Serialize(data);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await client.PatchAsync(firebaseUrl, content); // Patch kullanarak veriyi Güncelliyoruz
                }
            }
            catch { }
        }

        private async Task PushWarpTelemetryAsync()
        {
            if (string.IsNullOrEmpty(_deviceId)) return;
            try
            {
                string firebaseUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/users/{_deviceId}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                var data = new
                {
                    warp_active = (_warpManager != null && _warpManager.IsActive) || WarpManager.IsWireSockServiceRunning(),
                    warp_started_at = _warpManager.WarpStartedAt?.ToString("o") ?? "",
                    warp_stopped_at = _warpManager.WarpStoppedAt?.ToString("o") ?? "",
                    warp_total_hours_today = Math.Round(_warpManager.WarpTotalHoursToday, 2)
                };
                string json = JsonSerializer.Serialize(data);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PatchAsync(firebaseUrl, content);
                try
                {
                    string atUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/alptogether/users/{_deviceId}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                    var atData = new
                    {
                        warpActive = (_warpManager != null && _warpManager.IsActive) || WarpManager.IsWireSockServiceRunning(),
                        warp_active = (_warpManager != null && _warpManager.IsActive) || WarpManager.IsWireSockServiceRunning(),
                        warpStartedAt = _warpManager?.WarpStartedAt?.ToString("o") ?? "",
                        warpStoppedAt = _warpManager?.WarpStoppedAt?.ToString("o") ?? "",
                        warpTotalHoursToday = Math.Round(_warpManager?.WarpTotalHoursToday ?? 0, 2),
                        version = BASE_VERSION,
                        machine_name = Environment.MachineName,
                        pc_user = Environment.UserName,
                        last_seen = DateTime.UtcNow.ToString("o"),
                        installedAppVersions = new Dictionary<string, string>
                        {
                            ["alpnet-pulse"] = BASE_VERSION,
                            ["alpnet"] = BASE_VERSION
                        }
                    };
                    string atJson = JsonSerializer.Serialize(atData);
                    using var atContent = new StringContent(atJson, Encoding.UTF8, "application/json");
                    await _httpClient.PatchAsync(atUrl, atContent);
                }
                catch { }
            }
            catch { }
        }
        #endregion

        #region Wi-Fi Availability & Visibility
        private DateTime _lastWifiVisibilityCheck = DateTime.MinValue;

        private void CheckAndUpdateWifiVisibility()
        {
            try
            {
                // Wi-Fi donanımı var mı ve radyo/bağlantı açık mı?
                bool isWifiAvailable = NativeWifi.IsWifiAvailableAndEnabled();

                Dispatcher.Invoke(() =>
                {
                    if (isWifiAvailable)
                    {
                        MenuBtnWifi.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        MenuBtnWifi.Visibility = Visibility.Collapsed;

                        // Eğer kullanıcı o sırada Wi-Fi Analizörü sayfasındaysa ve Wi-Fi kapatıldıysa Dashboard'a yönlendir
                        if (PageWifi.Visibility == Visibility.Visible)
                        {
                            StopWifiAutoScanAndRadarAnimation();
                            PageWifi.Visibility = Visibility.Collapsed;
                            PageDashboard.Visibility = Visibility.Visible;
                            PageTitle.Text = "Gösterge Paneli";
                            PageSubtitle.Text = "Gelişmiş Ethernet Aktarım Gücü & Sağlık Analizörü";
                            MenuBtnDashboard.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
                            MenuBtnDashboard.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
                            MenuBtnWifi.Background = System.Windows.Media.Brushes.Transparent;
                            MenuBtnWifi.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                        }
                    }
                });
            }
            catch { }
        }
        #endregion

        #region Network Statistics & Diagnostics
        private void StatsUpdateTimer_Tick(object? sender, EventArgs e)
        {
            // Her 3 saniyede bir Wi-Fi modülü varlığını ve açık/kapalı durumunu bağımsız kontrol et (kullanıcı hangi sekmede olursa olsun)
            if ((DateTime.Now - _lastWifiVisibilityCheck).TotalSeconds >= 3)
            {
                _lastWifiVisibilityCheck = DateTime.Now;
                Task.Run(() => CheckAndUpdateWifiVisibility());
            }

            // İşlemci (CPU) Optimizasyonu: Uygulama arka plandayken veya başka sekmedeyken sürekli dashboard istatistik taraması yapma
            if (WindowState == WindowState.Minimized || PageDashboard.Visibility != Visibility.Visible || !this.IsActive)
            {
                return; 
            }

            if (_licenseState.Licensed)
            {
                UpdateNetworkStats();
            }
            
            if (UsbDevicesList.Visibility == Visibility.Visible)
            {
                UpdateUsbDevicesListAsync();
            }
        }

        private string _currentPingTarget = "1.1.1.1";

        public static bool IsVirtualOrFilterInterface(NetworkInterface ni)
        {
            if (ni == null) return false;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || 
                ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                return true;

            string name = ni.Name ?? "";
            string desc = ni.Description ?? "";
            string combined = (name + " " + desc).ToLowerInvariant();

            if (name.Contains('*')) return true;

            string[] virtualKeywords = new[]
            {
                "virtual", "hyper-v", "vmware", "vbox", "virtualbox", 
                "vethernet", "vswitch", "tap", "tun", "wireguard", 
                "wiresock", "hamachi", "zerotier", "tailscale", "radmin", 
                "bluetooth", "pseudo", "loopback", "miniport", "wan", 
                "filter", "lightweight", "lwf", "wfp", "kernel debug", 
                "ndis", "p2p", "direct virtual", "host-only", "teredo", 
                "isatap", "6to4", "npcap", "winpcap"
            };

            return virtualKeywords.Any(k => combined.Contains(k));
        }

        private void UpdateNetworkStats()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                
                // Get active interface
                var activeNi = GetActiveNetworkInterface(interfaces);

                // Update physical adapters list
                UpdateAdaptersList(interfaces, activeNi);

                if (activeNi == null)
                {
                    LblConnectionType.Text = "Bağlantı Yok";
                    LblConnectionType.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                    CardSpeedVal.Text = "Bağlantı Yok";
                    CardStandardVal.Text = "Kablo Takılı Değil";
                    CardGatewayVal.Text = "Ağ Geçidi Yok";
                    CardHealthVal.Text = "Kablo Yok";
                    CardHealthVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                    NicFatigueVal.Text = "0 Hata / 0 Kayıp";
                    NicFatigueVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                    WifiWarningBanner.Visibility = Visibility.Collapsed;
                    return;
                }

                // Connection Type
                bool isWifi = activeNi.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                WifiWarningBanner.Visibility = isWifi ? Visibility.Visible : Visibility.Collapsed;
                LblConnectionType.Text = isWifi ? "Wi-Fi" : "Ethernet";
                LblConnectionType.Foreground = isWifi ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 153, 0)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));

                // Speed (Link Speed)
                long speedBits = activeNi.Speed; // bps
                string speedText = FormatSpeed(speedBits);
                CardSpeedVal.Text = speedText;

                // Cable Standard
                CardStandardVal.Text = GetCableStandard(speedBits, isWifi);

                // Gateway IP & MAC Address
                var ipProperties = activeNi.GetIPProperties();
                
                // DNS Resolver for Ping Target
                var dnsAddresses = ipProperties.DnsAddresses;
                string activeDns = dnsAddresses.Count > 0 ? dnsAddresses[0].ToString() : "1.1.1.1";
                string dnsLabel = "Özel DNS";
                if (activeDns == "8.8.8.8" || activeDns == "8.8.4.4") dnsLabel = "Google DNS";
                else if (activeDns == "1.1.1.1" || activeDns == "1.0.0.1") dnsLabel = "Cloudflare DNS";
                else if (activeDns.StartsWith("192.168.") || activeDns.StartsWith("10.") || activeDns.StartsWith("172.")) dnsLabel = "Yerel Ağ Geçidi";
                else if (activeDns == "::1" || activeDns == "127.0.0.1") dnsLabel = "Localhost";
                
                string newPingTargetText = $"{activeDns} ({dnsLabel})";
                if (PingTargetVal.Text != newPingTargetText)
                {
                    PingTargetVal.Text = newPingTargetText;
                    _currentPingTarget = activeDns;
                }
                
                string currentDnsText = $"Şu anki: {dnsLabel}";
                if (DnsStatusVal.Text != currentDnsText && !DnsStatusVal.Text.Contains("Uygulanıyor") && !DnsStatusVal.Text.Contains("Hata"))
                {
                    DnsStatusVal.Text = currentDnsText;
                    DnsStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                }

                var gatewayAddress = ipProperties.GatewayAddresses.FirstOrDefault()?.Address;
                if (gatewayAddress != null)
                {
                    string gatewayMac = GetMacAddressürümArp(gatewayAddress);
                    CardGatewayVal.Text = $"{gatewayAddress}\n{gatewayMac}";
                }
                else
                {
                    CardGatewayVal.Text = "Ağ Geçidi Yok";
                }

                // Health (RX/TX errors/drops)
                var ipStats = activeNi.GetIPv4Statistics();
                long totalErrors = ipStats.IncomingPacketsWithErrors + ipStats.OutgoingPacketsWithErrors;
                long totalDropped = ipStats.IncomingPacketsDiscarded + ipStats.OutgoingPacketsDiscarded;

                NicFatigueVal.Text = $"{totalErrors} Hata / {totalDropped} Kayıp";
                
                if (totalErrors == 0 && totalDropped == 0)
                {
                    CardHealthVal.Text = "Mükemmel";
                    CardHealthVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                    NicFatigueVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                }
                else if (totalErrors < 5 && totalDropped < 10)
                {
                    CardHealthVal.Text = "Kararlı";
                    CardHealthVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 204, 0));
                    NicFatigueVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 204, 0));
                }
                else
                {
                    CardHealthVal.Text = "Hatalı Port";
                    CardHealthVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                    NicFatigueVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Network stats update error: " + ex.Message);
            }
        }

        private NetworkInterface? GetActiveNetworkInterface(NetworkInterface[] interfaces)
        {
            // Filter: operationalstatus = Up, not loopback, not virtual/tunnel, has default gateway
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up && 
                    !IsVirtualOrFilterInterface(ni))
                {
                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Any(g => g.Address != null && 
                                                       !string.IsNullOrEmpty(g.Address.ToString()) && 
                                                       g.Address.ToString() != "0.0.0.0"))
                    {
                        return ni;
                    }
                }
            }

            // Fallback: only if a physical Ethernet or Wi-Fi interface is genuinely Up and has IPv4 assigned
            return interfaces.FirstOrDefault(ni => 
                ni.OperationalStatus == OperationalStatus.Up && 
                (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                !IsVirtualOrFilterInterface(ni) &&
                ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && 
                                                               !u.Address.ToString().StartsWith("169.254.")));
        }

        private string FormatSpeed(long speedBits)
        {
            if (speedBits <= 0) return "Bilinmiyor";

            double speedMbps = speedBits / 1_000_000.0;
            if (speedMbps >= 1000)
            {
                return $"{(speedMbps / 1000.0):0.##} Gbps";
            }
            return $"{speedMbps:0} Mbps";
        }

        private string _wifiSignal = "Wi-Fi Aktarımı";
        private DateTime _lastWifiCheck = DateTime.MinValue;

        private string GetCableStandard(long speedBits, bool isWifi)
        {
            if (isWifi)
            {
                if ((DateTime.Now - _lastWifiCheck).TotalSeconds >= 0.8)
                {
                    _lastWifiCheck = DateTime.Now;
                    try
                    {
                        _wifiSignal = NativeWifi.GetConnectedWifiSignal();
                    }
                    catch { }
                }
                return _wifiSignal;
            }

            double speedMbps = speedBits / 1_000_000.0;
            if (speedMbps <= 10) return "Max ~100m (Cat3/5)";
            if (speedMbps <= 100) return "Max ~100m (Cat5)";
            if (speedMbps <= 1000) return "Max ~100m (Cat5e/Cat6)";
            if (speedMbps <= 2500) return "Max ~100m (Cat6)";
            if (speedMbps <= 10000) return "Max ~55-100m (Cat6/Cat6a)";
            return "Fiber/Özel Hat";
        }

        private string GetMacAddressürümArp(IPAddress ipAddress)
        {
            try
            {
                byte[] macAddr = new byte[6];
                uint macAddrLen = (uint)macAddr.Length;
                
                // Parse destination IP to uint
                byte[] ipBytes = ipAddress.GetAddressBytes();
                if (ipBytes.Length != 4) return "Bilinmiyor"; // IPv4 only for ARP

                uint destIp = BitConverter.ToUInt32(ipBytes, 0);

                int result = SendARP(destIp, 0, macAddr, ref macAddrLen);
                if (result == 0)
                {
                    return BitConverter.ToString(macAddr, 0, (int)macAddrLen).Replace("-", ":");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ARP Send error: " + ex.Message);
            }

            // Fallback: parse console arp -a output (just in case SendARP fails)
            return QueryArpTable(ipAddress.ToString());
        }

        private string QueryArpTable(string ip)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "arp",
                        Arguments = "-a",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                string[] lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains(ip))
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            return parts[1].ToUpper().Replace("-", ":");
                        }
                    }
                }
            }
            catch { }
            return "Bilinmiyor";
        }

        private async Task FetchGeoIpAndNetworkDetails()
        {
            // Public IP via api
            try
            {
                string publicIp = await _httpClient.GetStringAsync("https://api.ipify.org");
                if (!string.IsNullOrWhiteSpace(publicIp))
                {
                    Dispatcher.Invoke(() =>
                    {
                        LblPublicIp.Text = publicIp.Trim();
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    LblPublicIp.Text = "Hata (Çevrimdışı)";
                });
                Debug.WriteLine("GeoIP fetch error: " + ex.Message);
            }

            // DNS Servers fetching removed from UI update
            try
            {
                var activeNi = GetActiveNetworkInterface(NetworkInterface.GetAllNetworkInterfaces());
            }
            catch
            {
                // Ignore
            }
        }

        private void UpdateAdaptersList(NetworkInterface[] interfaces, NetworkInterface? activeNi)
        {
            var physicalList = new List<AdapterUiItem>();
            var virtualList = new List<AdapterUiItem>();
            foreach (var ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || 
                    ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    bool isActive = activeNi != null && ni.Id == activeNi.Id;
                    bool isVirtual = IsVirtualOrFilterInterface(ni);
                    
                    int sortOrder = 2;
                    if (isActive) sortOrder = 0;
                    else if (isVirtual) sortOrder = 3;
                    else if (ni.OperationalStatus == OperationalStatus.Up) sortOrder = 1;

                    string statusStr = "Kablo Takılı Değil";
                    System.Windows.Media.Color bgCol = System.Windows.Media.Color.FromArgb(12, 255, 255, 255);
                    System.Windows.Media.Color fgCol = System.Windows.Media.Color.FromRgb(173, 181, 189);
                    string? toolTipStr = null;

                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        try 
                        {
                            var stats = ni.GetIPv4Statistics();
                            if (stats.IncomingPacketsWithErrors > 0 || stats.OutgoingPacketsWithErrors > 0)
                            {
                                statusStr = "Bağlı (Hasarlı/Paket Kaybı)";
                                bgCol = System.Windows.Media.Color.FromArgb(38, 255, 8, 68);
                                fgCol = System.Windows.Media.Color.FromRgb(255, 8, 68);
                                toolTipStr = "Bu ağda veri paketleri kayboluyor veya hatalı ulaşıyor. Kabloda veya donanımda fiziksel hasar olabilir.";
                            }
                            else
                            {
                                statusStr = isActive ? "Aktif (Kullanımda)" : "Bağlı";
                                bgCol = isActive ? System.Windows.Media.Color.FromArgb(38, 0, 255, 135) : System.Windows.Media.Color.FromArgb(12, 255, 255, 255);
                                fgCol = isActive ? System.Windows.Media.Color.FromRgb(0, 255, 135) : System.Windows.Media.Color.FromRgb(173, 181, 189);
                            }
                        }
                        catch 
                        {
                            // If IPv4 statistics fail
                            statusStr = isActive ? "Aktif (Kullanımda)" : "Bağlı";
                            bgCol = isActive ? System.Windows.Media.Color.FromArgb(38, 0, 255, 135) : System.Windows.Media.Color.FromArgb(12, 255, 255, 255);
                            fgCol = isActive ? System.Windows.Media.Color.FromRgb(0, 255, 135) : System.Windows.Media.Color.FromRgb(173, 181, 189);
                        }
                    }
                    else if (ni.OperationalStatus == OperationalStatus.Testing)
                    {
                        statusStr = "Test Ediliyor";
                        fgCol = System.Windows.Media.Color.FromRgb(255, 204, 0);
                    }
                    else if (ni.OperationalStatus == OperationalStatus.Dormant)
                    {
                        statusStr = "Beklemede (Uyku)";
                    }
                    else if (ni.OperationalStatus == OperationalStatus.NotPresent)
                    {
                        statusStr = "Donanım Eksik / Hasarlı";
                        bgCol = System.Windows.Media.Color.FromArgb(38, 255, 8, 68);
                        fgCol = System.Windows.Media.Color.FromRgb(255, 8, 68);
                        toolTipStr = "Bu donanım bilgisayara fiziksel olarak bağlı değil veya donanım sürücüsünde ciddi bir arıza (sürücü bozulması/çakışması) var.";
                    }
                    else if (ni.OperationalStatus == OperationalStatus.LowerLayerDown)
                    {
                        statusStr = "Alt Katman Çöktü";
                        bgCol = System.Windows.Media.Color.FromArgb(38, 255, 8, 68);
                        fgCol = System.Windows.Media.Color.FromRgb(255, 8, 68);
                        toolTipStr = "Ağ donanımı alt katmanla haberleşemiyor. Sürücü kaynaklı bir arıza olabilir.";
                    }

                    var adapterItem = new AdapterUiItem
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        Status = statusStr,
                        StatusBg = new SolidColorBrush(bgCol),
                        StatusFg = new SolidColorBrush(fgCol),
                        SortOrder = sortOrder,
                        ToolTipText = toolTipStr
                    };

                    if (isVirtual)
                        virtualList.Add(adapterItem);
                    else
                        physicalList.Add(adapterItem);
                }
            }

            ActiveAdaptersList.ItemsSource = physicalList.Where(x => x.SortOrder == 0).ToList();
            AdaptersList.ItemsSource = physicalList.Where(x => x.SortOrder != 0).OrderBy(x => x.SortOrder).ToList();
            VirtualAdaptersList.ItemsSource = virtualList.OrderBy(x => x.SortOrder).ToList();
        }

        private void ToggleVirtualAdaptersBtn_Click(object sender, RoutedEventArgs e)
        {
            if (VirtualAdaptersList.Visibility == Visibility.Collapsed)
            {
                VirtualAdaptersList.Visibility = Visibility.Visible;
                ToggleVirtualAdaptersBtn.Content = "Sanal Bilgisayar Bağlantılarını Gizle ▲";
            }
            else
            {
                VirtualAdaptersList.Visibility = Visibility.Collapsed;
                ToggleVirtualAdaptersBtn.Content = "Sanal Bilgisayar Bağlantılarını Göster ▼";
            }
        }

        private void ToggleUsbDevicesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (UsbDevicesList.Visibility == Visibility.Collapsed)
            {
                UsbDevicesList.Visibility = Visibility.Visible;
                ToggleUsbDevicesBtn.Content = "🖱️ Çevre Birimlerini (USB/Mouse/Klavye) Gizle ▲";
                UpdateUsbDevicesListAsync();
            }
            else
            {
                UsbDevicesList.Visibility = Visibility.Collapsed;
                ToggleUsbDevicesBtn.Content = "🖱️ Çevre Birimlerini (USB/Mouse/Klavye) Göster ▼";
            }
        }

        private bool IsSystemUsbName(string name, string deviceId)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string n = name.ToLowerInvariant();
            
            // Eğer cihazın ID'sinde "VID_" geçmiyorsa, bu USB değil, anakart üzerindeki I2C/ACPI HID donanımlarıdır. (Örn: INTC816, UNIW0001)
            // Bunları kullanıcıya Dongle olarak göstermek kafa karıştırır.
            if (!string.IsNullOrEmpty(deviceId) && !deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Completely hide motherboards / internal root hubs / system devices / intel event filters
            return n.Contains("hub") || n.Contains("denetleyici") || n.Contains("host controller") || 
                   n.Contains("root") || n.Contains("bluetooth") || n.Contains("microsoft input") || 
                   n.Contains("numaralandırıcı") || n.Contains("event filter") || n.Contains("sistem");
        }

        private bool IsGenericUsbName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string n = name.ToLowerInvariant();
            // Generic dongles and unnamed mice/keyboards
            return n.Contains("hid") || n.Contains("usb giriş") || n.Contains("usb input") || 
                   n.Contains("bileşik") || n.Contains("composite") || n.Contains("uyumlu") || 
                   n.Contains("aygıt") || n.Contains("cihaz");
        }

        private class TempUsbInfo
        {
            public string Name { get; set; } = "";
            public string DeviceId { get; set; } = "";
            public string DisplayId { get; set; } = "";
            public string Status { get; set; } = "";
            public string Description { get; set; } = "";
            public int ErrorCode { get; set; } = 0;
        }

        private async void UpdateUsbDevicesListAsync()
        {
            var usbList = new List<AdapterUiItem>();
            
            await Task.Run(() =>
            {
                try
                {
                    var tempItems = new List<TempUsbInfo>();

                    using (var searcher = new ManagementObjectSearcher("SELECT Name, DeviceID, Status, ConfigManagerErrorCode, Description FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%' OR DeviceID LIKE 'HID%'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string name = obj["Name"]?.ToString() ?? "Bilinmeyen Aygıt";
                            string deviceId = obj["DeviceID"]?.ToString() ?? "";
                            
                            // Hızlı filtreleme: Sistem donanımıysa listeye hiç ekleme!
                            if (IsSystemUsbName(name, deviceId)) continue;
                            
                            string status = obj["Status"]?.ToString() ?? "Unknown";
                            string description = obj["Description"]?.ToString() ?? "";
                            string? errorCodeStr = obj["ConfigManagerErrorCode"]?.ToString();
                            
                            int errorCode = 0;
                            if (int.TryParse(errorCodeStr, out int parsedCode))
                            {
                                errorCode = parsedCode;
                            }

                            string displayId = deviceId;
                            int slashIdx = displayId.IndexOf('\\');
                            if (slashIdx >= 0 && slashIdx < displayId.Length - 1)
                            {
                                string afterSlash = displayId.Substring(slashIdx + 1);
                                int slash2 = afterSlash.IndexOf('\\');
                                if (slash2 > 0)
                                    displayId = afterSlash.Substring(0, slash2); // gets VID_XXXX&PID_XXXX
                                else
                                    displayId = afterSlash;
                                    
                                // Remove interface index (&MI_xx) so that composite device interfaces group together
                                int miIdx = displayId.IndexOf("&MI_");
                                if (miIdx > 0)
                                {
                                    displayId = displayId.Substring(0, miIdx);
                                }
                            }

                            tempItems.Add(new TempUsbInfo
                            {
                                Name = name,
                                DeviceId = deviceId,
                                DisplayId = displayId,
                                Status = status,
                                Description = description,
                                ErrorCode = errorCode
                            });
                        }
                    }

                    // Group by DisplayId (VID/PID)
                    var grouped = tempItems.GroupBy(x => x.DisplayId);

                    foreach (var group in grouped)
                    {
                        // Prefer non-generic names, fallback to the first generic one
                        var bestItem = group.OrderBy(x => IsGenericUsbName(x.Name) ? 1 : 0).ThenByDescending(x => x.Name.Length).First();
                        
                        string statusStr = "Çalışıyor";
                        System.Windows.Media.Color bgCol = System.Windows.Media.Color.FromArgb(38, 0, 255, 135);
                        System.Windows.Media.Color fgCol = System.Windows.Media.Color.FromRgb(0, 255, 135);
                        string? toolTipStr = null;

                        // If any interface has an error, mark the whole device as error
                        int worstErrorCode = group.Max(x => x.ErrorCode);
                        bool hasErrorStatus = group.Any(x => x.Status.Equals("Error", StringComparison.OrdinalIgnoreCase));

                        if (worstErrorCode != 0 || hasErrorStatus)
                        {
                            statusStr = $"Hatalı (Kod: {worstErrorCode})";
                            bgCol = System.Windows.Media.Color.FromArgb(38, 255, 8, 68);
                            fgCol = System.Windows.Media.Color.FromRgb(255, 8, 68);
                            toolTipStr = "Bu cihaz düzgün çalışmıyor (Hata Kodu: " + worstErrorCode + "). Sürücü hatası, donanım arızası veya yeterli güç alamama sorunu olabilir.";
                        }
                        
                        string finalName = bestItem.Name;
                        if (IsGenericUsbName(finalName))
                        {
                            // İsimsiz klavye/fare veya 2.4GHz Dongle.
                            // Gerçek ismini okuyamadığımız için jenerik ismi dongle olduğunu belli edecek şekilde değiştiriyoruz.
                            finalName = "USB 2.4GHz Alıcı / Dongle (" + finalName + ")";
                        }

                        string finalDesc = $"{group.Count()} Alt Aygıt/Arayüz";
                        if (bestItem.DeviceId.Contains("HID")) finalDesc += " | İnsan Arabirim Aygıtı";
                        else finalDesc += " | USB Aygıtı";

                        var bgBrush = new SolidColorBrush(bgCol);
                        bgBrush.Freeze();
                        var fgBrush = new SolidColorBrush(fgCol);
                        fgBrush.Freeze();

                        usbList.Add(new AdapterUiItem
                        {
                            Name = finalName,
                            Description = finalDesc + " | ID: " + bestItem.DisplayId,
                            Status = statusStr,
                            StatusBg = bgBrush,
                            StatusFg = fgBrush,
                            SortOrder = worstErrorCode != 0 ? 0 : 1, // Errors at top
                            ToolTipText = toolTipStr
                        });
                    }
                }
                catch (Exception)
                {
                    // Ignore WMI errors
                }
            });

            Dispatcher.Invoke(() =>
            {
                UsbDevicesList.ItemsSource = usbList.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToList();
            });
        }
        #endregion

        #region Latency & Speed Test Operations
        private async void RunPingTest_Click(object sender, RoutedEventArgs e)
        {
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "TEST EDİLİYOR...";
            
            PingLossVal.Text = "Ölçülüyor...";
            PingLatencyVal.Text = "Ölçülüyor...";

            string target = _currentPingTarget;
            int count = 4;
            int successfulPings = 0;
            long totalRoundtripTime = 0;

            await Task.Run(() =>
            {
                using (var ping = new Ping())
                {
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            var reply = ping.Send(target, 1000);
                            if (reply.Status == IPStatus.Success)
                            {
                                successfulPings++;
                                totalRoundtripTime += reply.RoundtripTime;
                            }
                        }
                        catch { }
                        Thread.Sleep(200); // short delay between pings
                    }
                }
            });

            int lossPercent = ((count - successfulPings) * 100) / count;
            PingLossVal.Text = $"{lossPercent}% Kayıp";
            PingLossVal.Foreground = lossPercent == 0 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));

            if (successfulPings > 0)
            {
                long avgLatency = totalRoundtripTime / successfulPings;
                PingLatencyVal.Text = $"{avgLatency} ms";
                PingLatencyVal.Foreground = avgLatency < 50 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 204, 0));
            }
            else
            {
                PingLatencyVal.Text = "Zaman Aşımı";
                PingLatencyVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
            }

            btn.IsEnabled = true;
            btn.Content = "PING TESTİNİ BAŞLAT";
        }

        private async void RunStressTest_Click(object sender, RoutedEventArgs e)
        {
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "STRES TESTİ DEVAM EDİYOR...";
            
            PingLossVal.Text = "Stres...";
            PingLatencyVal.Text = "Stres...";
            NicFatigueVal.Text = "Zorlanıyor...";

            int count = 100;
            int successfulPings = 0;
            long totalRoundtripTime = 0;

            await Task.Run(() =>
            {
                // Find default gateway to flood
                string gateway = "1.1.1.1"; // Fallback
                try
                {
                    var card = GetActiveNetworkInterface(NetworkInterface.GetAllNetworkInterfaces());
                    if (card != null)
                    {
                        var props = card.GetIPProperties();
                        var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        if (gw != null)
                        {
                            gateway = gw.Address.ToString();
                        }
                    }
                }
                catch { }

                // Modern routers block 1472-byte parallel ping floods as DoS. Use Standard 64 bytes.
                byte[] buffer = new byte[64]; 
                new Random().NextBytes(buffer);
                PingOptions options = new PingOptions(64, true);

                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 5 }, i =>
                {
                    try
                    {
                        using (var p = new Ping())
                        {
                            var reply = p.Send(gateway, 2000, buffer, options);
                            if (reply.Status == IPStatus.Success)
                            {
                                Interlocked.Increment(ref successfulPings);
                                Interlocked.Add(ref totalRoundtripTime, reply.RoundtripTime);
                            }
                        }
                        Thread.Sleep(10); // Small delay to avoid triggering flood protection
                    }
                    catch { }
                });
            });

            int lossPercent = ((count - successfulPings) * 100) / count;
            PingLossVal.Text = $"Stres Sonucu: %{lossPercent} Kayıp";
            PingLossVal.Foreground = lossPercent < 5 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));

            if (successfulPings > 0)
            {
                long avgLatency = totalRoundtripTime / successfulPings;
                PingLatencyVal.Text = $"Stres Gecikmesi: {avgLatency} ms";
                PingLatencyVal.Foreground = avgLatency < 15 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 204, 0));
            }
            else
            {
                PingLatencyVal.Text = "AÄ ÇÖKTÜ";
                PingLatencyVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
            }

            btn.IsEnabled = true;
            btn.Content = "MODEM STRES TESTİ (LOKAL BANT)";
        }

        private async void RunSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "ÖLÇÜLÜYOR...";
            SpeedProgressBar.Width = 0;

            SpeedDownloadVal.Text = "0.00 Mbps";
            SpeedUploadVal.Text = "0.00 Mbps";

            bool downloadError = false;
            bool uploadError = false;

            try
            {
                // 1. Download Test
                SpeedStatusVal.Text = "İndirme Testi Başlıyor...";
                double downloadSpeed = 0;
                try
                {
                    // Download 50MB chunk from Cloudflare CDN
                    string dlUrl = "https://speed.cloudflare.com/__down?bytes=50000000";
                    
                    using (var response = await _httpClient.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? 50_000_000;
                        var contentStream = await response.Content.ReadAsStreamAsync();
                        var stopwatch = Stopwatch.StartNew();
                        
                        byte[] buffer = new byte[81920];
                        long bytesReadTotal = 0;
                        int bytesRead;
                        long lastUiUpdateMs = 0;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            bytesReadTotal += bytesRead;
                            long elapsedMs = stopwatch.ElapsedMilliseconds;

                            if (elapsedMs - lastUiUpdateMs >= 60 || bytesReadTotal >= totalBytes)
                            {
                                lastUiUpdateMs = elapsedMs;
                                double currentProgress = (double)bytesReadTotal / totalBytes;
                                long currentTotal = bytesReadTotal;
                                double currentSec = stopwatch.Elapsed.TotalSeconds;

                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    double maxWidth = SpeedProgressContainer.ActualWidth;
                                    if (maxWidth <= 0) maxWidth = 190;
                                    SpeedProgressBar.Width = Math.Min(currentProgress * maxWidth, maxWidth);
                                    if (currentSec > 0)
                                    {
                                        double currentSpeedMbps = (currentTotal * 8.0) / (currentSec * 1_000_000.0);
                                        SpeedDownloadVal.Text = $"{currentSpeedMbps:0.00} Mbps";
                                    }
                                }), DispatcherPriority.Background);
                            }
                        }
                        stopwatch.Stop();
                        downloadSpeed = (bytesReadTotal * 8.0) / (stopwatch.Elapsed.TotalSeconds * 1_000_000.0);
                        SpeedDownloadVal.Text = $"{downloadSpeed:0.00} Mbps";
                    }
                }
                catch (Exception ex)
                {
                    downloadError = true;
                    SpeedStatusVal.Text = "İndirme hatası!";
                    Debug.WriteLine("Download test error: " + ex.Message);
                }

                // Short pause
                await Task.Delay(500);

                // 2. Upload Test
                SpeedStatusVal.Text = "Yükleme Testi Başlıyor...";
                SpeedProgressBar.Width = 0;
                double uploadSpeed = 0;
                try
                {
                    // Generate 10MB payload to post
                    byte[] uploAndata = new byte[10 * 1024 * 1024];
                    new Random().NextBytes(uploAndata);

                    string upUrl = "https://speed.cloudflare.com/__up";
                    var stopwatch = Stopwatch.StartNew();
                    long lastUploadUiUpdateMs = 0;

                    var request = new HttpRequestMessage(HttpMethod.Post, upUrl);
                    request.Content = new ProgressStreamContent(uploAndata, (uploadedBytes) =>
                    {
                        long elapsedMs = stopwatch.ElapsedMilliseconds;
                        if (elapsedMs - lastUploadUiUpdateMs >= 60 || uploadedBytes >= uploAndata.Length)
                        {
                            lastUploadUiUpdateMs = elapsedMs;
                            double currentProgress = (double)uploadedBytes / uploAndata.Length;
                            double currentSec = stopwatch.Elapsed.TotalSeconds;

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                double maxWidth = SpeedProgressContainer.ActualWidth;
                                if (maxWidth <= 0) maxWidth = 190;
                                SpeedProgressBar.Width = Math.Min(currentProgress * maxWidth, maxWidth);
                                if (currentSec > 0)
                                {
                                    double currentSpeedMbps = (uploadedBytes * 8.0) / (currentSec * 1_000_000.0);
                                    SpeedUploadVal.Text = $"{currentSpeedMbps:0.00} Mbps";
                                }
                            }), DispatcherPriority.Background);
                        }
                    });
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    stopwatch.Stop();
                    Dispatcher.Invoke(() => {
                        double maxWidth = SpeedProgressContainer.ActualWidth;
                        if (maxWidth <= 0) maxWidth = 190;
                        SpeedProgressBar.Width = maxWidth;
                    });
                    uploadSpeed = (uploAndata.Length * 8.0) / (stopwatch.Elapsed.TotalSeconds * 1_000_000.0);
                    SpeedUploadVal.Text = $"{uploadSpeed:0.00} Mbps";
                }
                catch (Exception ex)
                {
                    uploadError = true;
                    SpeedStatusVal.Text = "Yükleme hatası!";
                    Debug.WriteLine("Upload test error: " + ex.Message);
                }

                if (!downloadError && !uploadError)
                {
                    SpeedStatusVal.Text = "Test Tamamlandı";
                }
                else if (downloadError && uploadError)
                {
                    SpeedStatusVal.Text = "Hız testi başarısız oldu";
                }
                else if (downloadError)
                {
                    SpeedStatusVal.Text = "İndirme testi başarısız oldu";
                }
                else
                {
                    SpeedStatusVal.Text = "Yükleme testi başarısız oldu";
                }

                // Bar'ı sıfırla
                await Task.Delay(800);
                Dispatcher.Invoke(() => SpeedProgressBar.Width = 0);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "HIZ TESTİNİ BAŞLAT";
            }
        }

        private async void RunNetworkRepair_Click(object sender, RoutedEventArgs e)
        {
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100));
            OverlayGrid.Visibility = Visibility.Visible;
            OverlayStatusText.Text = "Onarım Başlıyor...";
            RepairStatusVal.Text = "Onarım Başlıyor...";

            try
            {
                string[] commands = new string[]
                {
                    "ipconfig /flushdns",
                    "ipconfig /release",
                    "ipconfig /renew",
                    "netsh winsock reset",
                    "netsh int ip reset"
                };

                string[] statusMessages = new string[]
                {
                    "DNS Önbelleği Temizleniyor...",
                    "Mevcut IP Bırakılıyor...",
                    "Yeni IP Alınıyor...",
                    "Ağ Soketleri Sıfırlanıyor...",
                    "TCP/IP Ayarları Sıfırlanıyor..."
                };

                for (int i = 0; i < commands.Length; i++)
                {
                    OverlayStatusText.Text = statusMessages[i];
                    RepairStatusVal.Text = "TCP/IP Ayarları Sıfırlanıyor...";
                    await Task.Run(() =>
                    {
                        try
                        {
                            var processInfo = new ProcessStartInfo("cmd.exe", "/c " + commands[i])
                            {
                                WindowStyle = ProcessWindowStyle.Hidden,
                                UseShellExecute = true,
                                Verb = "runas"
                            };
                            var process = Process.Start(processInfo);
                            process?.WaitForExit();
                        }
                        catch { }
                    });
                    await Task.Delay(1200);
                }

                // Step: Discord & App Socket/Cache Cleanup
                OverlayStatusText.Text = "Discord & Ağ İletişim Önbellekleri Temizleniyor...";
                RepairStatusVal.Text = "Discord & Ağ İletişim Önbellekleri Temizleniyor...";
                try
                {
                    await WarpManager.RepairDiscordCacheAsync(msg =>
                    {
                        Dispatcher.Invoke(() => OverlayStatusText.Text = msg);
                    });
                }
                catch { }

                // Step 6: Automatic DNS Troubleshooter & Optimal DNS Selection
                OverlayStatusText.Text = "DNS Hataları Onarılıyor & En Hızlı DNS Seçiliyor...";
                RepairStatusVal.Text = "DNS Hataları Onarılıyor & En Hızlı DNS Seçiliyor...";

                await Task.Run(async () =>
                {
                    var candidateDns = new[]
                    {
                        new { Name = "Cloudflare (1.1.1.1)", Primary = "1.1.1.1", Secondary = "1.0.0.1" },
                        new { Name = "Google (8.8.8.8)", Primary = "8.8.8.8", Secondary = "8.8.4.4" },
                        new { Name = "Quad9 (9.9.9.9)", Primary = "9.9.9.9", Secondary = "149.112.112.112" }
                    };

                    string bestPrimary = "";
                    string bestSecondary = "";
                    string bestName = "";

                    foreach (var dns in candidateDns)
                    {
                        try
                        {
                            using (var ping = new System.Net.NetworkInformation.Ping())
                            {
                                var reply = await ping.SendPingAsync(dns.Primary, 1200);
                                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                                {
                                    bestPrimary = dns.Primary;
                                    bestSecondary = dns.Secondary;
                                    bestName = dns.Name;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(bestPrimary))
                    {
                        Dispatcher.Invoke(() => ChangeDns(bestPrimary, bestSecondary, $"{bestName} (Otomatik Seçildi)"));
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (!_isDoHActive) ToggleDoH_Click(sender, e);
                        });
                    }
                });

                RepairStatusVal.Text = "Onarım Tamamlandı!";
                RepairStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                
                OverlayGrid.Visibility = Visibility.Collapsed;
                System.Windows.MessageBox.Show("İnternet ayarları başarıyla sıfırlandı.\n\nDeğişikliklerin tam olarak etkili olabilmesi için bilgisayarınızı YENİDEN BAŞLATMANIZI tavsiye ederiz.", "Onarım Tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
                RepairStatusVal.Text = "Onarım Başarısız!";
                RepairStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                System.Windows.MessageBox.Show("Onarım sırasında bir hata oluştu: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                btn.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            }
        }

        private async void BtnRepairDiscord_Click(object sender, RoutedEventArgs e)
        {
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            var originalContent = btn.Content;
            btn.Content = "⏳ Onarılıyor...";
            OverlayGrid.Visibility = Visibility.Visible;
            OverlayStatusText.Text = "Discord Mesaj & Bağlantı Onarımı Başlatılıyor...";

            try
            {
                var result = await WarpManager.RepairDiscordCacheAsync(msg =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        OverlayStatusText.Text = msg;
                    });
                });

                await Task.Delay(1000);
                OverlayGrid.Visibility = Visibility.Collapsed;

                if (result)
                {
                    System.Windows.MessageBox.Show("Discord önbelleği temizlendi ve ağ durumu sıfırlandı.\nDiscord'u şimdi yeniden açabilirsiniz; mesaj yükleme ve bağlantı sorunları çözülecektir.", "Discord Onarımı Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show("Discord onarımı tamamlanırken bazı dosyalar temizlenemedi. Lütfen Discord'u tamamen kapatıp tekrar deneyin.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
                System.Windows.MessageBox.Show("Discord onarımı sırasında bir hata oluştu: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = originalContent;
            }
        }

        private async void RunNetworkNuke_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("Bu işlem ağ ile ilgili TÜM ayarlarınızı fabrika ayarlarına döndürecektir. Kayıtlı Wi-Fi şifreleriniz SİLİNEBİLİR.\n\nİnternetiniz tamamen kopacak ve bilgisayarınızı yeniden başlatmanız gerekecek.\n\nDevam etmek istiyor musunuz?", "Nükleer Sıfırlama Uyarısı ☢️", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "SIFIRLANIYOR...";
            OverlayGrid.Visibility = Visibility.Visible;
            OverlayStatusText.Text = "Nükleer Ağ Sıfırlaması Devrede...";
            RepairStatusVal.Text = "Nükleer Ağ Sıfırlaması Devrede... Lütfen Bekleyin.";
            RepairStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));

            try
            {
                string[] commands = new string[]
                {
                    "ipconfig /flushdns",
                    "ipconfig /release",
                    "ipconfig /renew",
                    "arp -d *",
                    "nbtstat -R",
                    "nbtstat -RR",
                    "netsh winsock reset",
                    "netsh int ip reset",
                    "netsh branchcache reset"
                };

                await Task.Run(() =>
                {
                    foreach (var cmd in commands)
                    {
                        Dispatcher.Invoke(() => OverlayStatusText.Text = cmd + " çalıştırılıyor...");
                        try
                        {
                            var processInfo = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                            {
                                WindowStyle = ProcessWindowStyle.Hidden,
                                UseShellExecute = true,
                                Verb = "runas"
                            };
                            var process = Process.Start(processInfo);
                            process?.WaitForExit();
                        }
                        catch { }
                    }
                });

                RepairStatusVal.Text = "Sıfırlama Tamamlandı. Yeniden Başlatma Gerekli!";
                OverlayGrid.Visibility = Visibility.Collapsed;
                System.Windows.MessageBox.Show("Nükleer Sıfırlama tamamlandı!\nLütfen ağ kartlarınızın tekrar çalışabilmesi için bilgisayarınızı ŞU AN YENİDEN BAŞLATIN.", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
                System.Windows.MessageBox.Show("Hata: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "NÜKLEER AÄ SIFIRLAMA â˜¢ï¸";
            }
        }

        #region DNS Changer
        private void SetDnsCloudflare_Click(object? sender, RoutedEventArgs? e)
        {
            ChangeDns("1.1.1.1", "1.0.0.1", "Cloudflare DNS");
        }

        private void SetDnsGoogle_Click(object? sender, RoutedEventArgs? e)
        {
            ChangeDns("8.8.8.8", "8.8.4.4", "Google DNS");
        }

        private void ResetDns_Click(object? sender, RoutedEventArgs? e)
        {
            ChangeDns("dhcp", "", "Otomatik (DHCP)");
        }

        private async void ChangeDns(string primary, string secondary, string label)
        {
            try
            {
                DnsStatusVal.Text = "Uygulanıyor...";
                DnsStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                await Task.Run(() =>
                {
                    // If switching off or setting to DHCP, kill any running DoH proxy processes
                    if (primary == "dhcp")
                    {
                        try { _dohProcess?.Kill(); } catch { }
                        foreach (var p in Process.GetProcessesByName("dnscrypt-proxy"))
                        {
                            try { p.Kill(); } catch { }
                        }
                    }

                    string pshCommand;
                    if (primary == "dhcp")
                    {
                        pshCommand = "Get-NetAdapter | Set-DnsClientServerAddress -ResetServerAddresses -ErrorAction SilentlyContinue; Get-NetAdapter | Set-DnsClientServerAddress -Family IPv6 -ResetServerAddresses -ErrorAction SilentlyContinue";
                    }
                    else
                    {
                        pshCommand = $"Get-NetAdapter | Where-Object {{$_.Status -eq 'Up'}} | Set-DnsClientServerAddress -ServerAddresses '{primary}','{secondary}' -ErrorAction SilentlyContinue; Get-NetAdapter | Where-Object {{$_.Status -eq 'Up'}} | Set-DnsClientServerAddress -Family IPv6 -ResetServerAddresses -ErrorAction SilentlyContinue";
                    }

                    var processInfo = new ProcessStartInfo("powershell.exe", $"-Command \"{pshCommand}\"")
                    {
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    var process = Process.Start(processInfo);
                    process?.WaitForExit();
                    
                    // Netsh fallback & Flush DNS
                    string netshCmd = primary == "dhcp" 
                        ? "/c netsh interface ip set dns name=\"Ethernet\" source=dhcp 2>nul & netsh interface ip set dns name=\"Wi-Fi\" source=dhcp 2>nul & netsh interface ipv6 set dns name=\"Ethernet\" source=dhcp 2>nul & netsh interface ipv6 set dns name=\"Wi-Fi\" source=dhcp 2>nul & ipconfig /flushdns"
                        : "/c ipconfig /flushdns";

                    var flushInfo = new ProcessStartInfo("cmd.exe", netshCmd) { WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = true, Verb = "runas" };
                    Process.Start(flushInfo)?.WaitForExit();
                });

                DnsStatusVal.Text = $"Şu anki: {label}";
                DnsStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                
                var settings = PulseAppSettings.Load();
                settings.AutoDnsMode = primary == "dhcp" ? "dhcp" : (primary == "1.1.1.1" ? "cloudflare" : (primary == "8.8.8.8" ? "google" : (primary == "76.76.2.2" ? "controld" : (primary == "127.0.0.1" ? "doh" : primary))));
                settings.Save();
            }
            catch (Exception ex)
            {
                DnsStatusVal.Text = "Hata Oluştu!";
                DnsStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                System.Windows.MessageBox.Show("DNS değiştirilemedi (Yönetici izni reddedildi): " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region Game Optimizer
        private bool _isGameModeEnabled = false;

        private async void ToggleGameMode_Click(object? sender, RoutedEventArgs? e)
        {
            BtnToggleGameMode.IsEnabled = false;

            if (!_isGameModeEnabled)
            {
                // Turn ON Game Mode
                GameModeStatusVal.Text = "Servisler durduruluyor...";
                bool success = await Task.Run(() => ToggleServices(false));
                
                if (success)
                {
                    _isGameModeEnabled = true;
                    UpdateDiscordPresence();
                    GameModeBadgeText.Text = "AÇIK";
                    GameModeBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                    GameModeBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 0, 255, 135));
                    BtnToggleGameMode.Content = "OYUN MODUNU KAPAT";
                    BtnToggleGameMode.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68)); // Red color
                    GameModeStatusVal.Text = "SİSTEM oyunlar için optimize edildi.";
                    GameModeStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                }
                else
                {
                    GameModeStatusVal.Text = "Başarısız! (Yönetici izni gerekli)";
                }
            }
            else
            {
                // Turn OFF Game Mode
                GameModeStatusVal.Text = "Servisler yeniden başlatılıyor...";
                bool success = await Task.Run(() => ToggleServices(true));
                
                if (success)
                {
                    _isGameModeEnabled = false;
                    UpdateDiscordPresence();
                    GameModeBadgeText.Text = "KAPALI";
                    GameModeBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                    GameModeBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 255, 8, 68));
                    BtnToggleGameMode.Content = "OYUN MODUNU AÇ";
                    BtnToggleGameMode.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 126, 234)); // Default Blue
                    GameModeStatusVal.Text = "SİSTEM stAndart haline döndü.";
                    GameModeStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                }
                else
                {
                    GameModeStatusVal.Text = "Başarısız! (Yönetici izni gerekli)";
                }
            }

            BtnToggleGameMode.IsEnabled = true;
        }

        private bool ToggleServices(bool start)
        {
            try
            {
                string action = start ? "start" : "stop";
                string[] services = { "wuauserv", "bits", "dosvc" }; // Windows Update, BITS, Delivery Optimization

                string pshCommand = "";
                foreach (var svc in services)
                {
                    pshCommand += $"{action}-service {svc} -ErrorAction SilentlyContinue; ";
                }

                if (!start) // Game Mode ON (Stopping background update services, enabling network priority & TCP NoDelay)
                {
                    // Firewall blocks for bandwidth hogs during gaming
                    pshCommand += "New-NetFirewallRule -DisplayName 'AlpNet_GameModeBlock' -Direction Outbound -Action Block -Program '*\\OneDrive.exe' -ErrorAction SilentlyContinue; ";

                    // Regedit NetworkThrottlingIndex (ffffffff = Disabled/Gaming) & SystemResponsiveness (0 = Max Game Priority)
                    pshCommand += "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name 'NetworkThrottlingIndex' -Value 0xffffffff -ErrorAction SilentlyContinue; ";
                    pshCommand += "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name 'SystemResponsiveness' -Value 0 -ErrorAction SilentlyContinue; ";

                    // Apply TCP NoDelay & TcpAckFrequency (Nagle Devre Dışı Bırakma)
                    SetTcpNoDelay(true);
                }
                else // Game Mode OFF (Restoring to default)
                {
                    // Remove Firewall blocks
                    pshCommand += "Remove-NetFirewallRule -DisplayName 'AlpNet_GameModeBlock' -ErrorAction SilentlyContinue; ";

                    // Regedit NetworkThrottlingIndex (10 = Default) & SystemResponsiveness (14 = Default)
                    pshCommand += "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name 'NetworkThrottlingIndex' -Value 10 -ErrorAction SilentlyContinue; ";
                    pshCommand += "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile' -Name 'SystemResponsiveness' -Value 14 -ErrorAction SilentlyContinue; ";

                    // Restore TCP defaults
                    SetTcpNoDelay(false);
                }

                var processInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{pshCommand}\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var process = Process.Start(processInfo);
                process?.WaitForExit(5000);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SetTcpNoDelay(bool enable)
        {
            try
            {
                using (var tcpipInterfaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", true))
                {
                    if (tcpipInterfaces != null)
                    {
                        foreach (string subKeyName in tcpipInterfaces.GetSubKeyNames())
                        {
                            using (var ifaceKey = tcpipInterfaces.OpenSubKey(subKeyName, true))
                            {
                                if (ifaceKey != null)
                                {
                                    if (enable)
                                    {
                                        // TCP NoDelay & Immediate ACK for ultra-low latency gaming
                                        ifaceKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                                        ifaceKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                                        // DÜZELTME: TcpDelAckTicks 0 yapılmamalıdır (0 geçersiz değerdir ve oyunlarda paket düşüşüne neden olur)
                                        try { ifaceKey.DeleteValue("TcpDelAckTicks", false); } catch { }
                                    }
                                    else
                                    {
                                        // Restore Windows defaults
                                        try { ifaceKey.DeleteValue("TcpAckFrequency", false); } catch { }
                                        try { ifaceKey.DeleteValue("TCPNoDelay", false); } catch { }
                                        try { ifaceKey.DeleteValue("TcpDelAckTicks", false); } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetTcpNoDelay error: {ex.Message}");
            }
        }
        #endregion
        #endregion

        #region AlpNet WARP Split Tunnel
        private Process? _bypassProcess;
        private bool _isBypassActive = false;
        private WarpManager _warpManager = new WarpManager();
        
        // DoH
        private Process? _dohProcess;
        private bool _isDoHActive = false;

        private void CleanUpBypassServicesAndDrivers()
        {
            try
            {
                _warpManager.StopWarp();

                if (_bypassProcess != null && !_bypassProcess.HasExited)
                {
                    try { _bypassProcess.Kill(); } catch { }
                }
                _bypassProcess = null;

                foreach (var p in Process.GetProcessesByName("goodbyedpi"))
                {
                    try { p.Kill(); } catch { }
                }

                string cmdArgs = "/c \"taskkill /F /IM goodbyedpi.exe 2>nul & sc.exe stop GoodbyeDPI 2>nul & sc.exe delete GoodbyeDPI 2>nul & sc.exe stop WinDivert 2>nul & sc.exe delete WinDivert 2>nul & sc.exe stop WinDivert14 2>nul & sc.exe delete WinDivert14 2>nul & sc.exe stop WinDivert1.4 2>nul & sc.exe delete WinDivert1.4 2>nul\"";
                var killPsi = new ProcessStartInfo("cmd.exe", cmdArgs)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(killPsi);
                proc?.WaitForExit(2000);
            }
            catch { }
        }

        private void ResetDnsToDhcpSync()
        {
            try
            {
                string pshCommand = "Get-NetAdapter | Set-DnsClientServerAddress -ResetServerAddresses -ErrorAction SilentlyContinue; Get-NetAdapter | Set-DnsClientServerAddress -Family IPv6 -ResetServerAddresses -ErrorAction SilentlyContinue; if (Get-Command Set-DnsClientDohServerAddress -ErrorAction SilentlyContinue) { Set-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -AutoUpgrade $false -ErrorAction SilentlyContinue; Set-DnsClientDohServerAddress -ServerAddress '1.0.0.1' -AutoUpgrade $false -ErrorAction SilentlyContinue; }; Clear-DnsClientCache; ipconfig /flushdns";
                var processInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{pshCommand}\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var process = Process.Start(processInfo);
                process?.WaitForExit(3000);

                // Netsh fallback & Flush DNS
                string netshCmd = "/c netsh interface ip set dns name=\"Ethernet\" source=dhcp 2>nul & netsh interface ip set dns name=\"Wi-Fi\" source=dhcp 2>nul & netsh interface ipv6 set dns name=\"Ethernet\" source=dhcp 2>nul & netsh interface ipv6 set dns name=\"Wi-Fi\" source=dhcp 2>nul & ipconfig /flushdns";
                var flushInfo = new ProcessStartInfo("cmd.exe", netshCmd)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(flushInfo)?.WaitForExit(2500);
            }
            catch { }
        }

        private int _cleanupExecuted = 0;

        public void PerformShutdownCleanup()
        {
            if (Interlocked.Exchange(ref _cleanupExecuted, 1) != 0) return;

            // 1. DoH Active Cleanup & DNS Restoration (Issue 2)
            bool hasLocalDns = false;
            try
            {
                hasLocalDns = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .Any(n => n.GetIPProperties().DnsAddresses.Any(d => d.ToString() == "127.0.0.1" || d.ToString() == "::1"));
            }
            catch { }

            if (_isDoHActive || hasLocalDns || _dohProcess != null)
            {
                try
                {
                    if (_dohProcess != null && !_dohProcess.HasExited)
                    {
                        try { _dohProcess.Kill(); } catch { }
                    }
                    _dohProcess = null;
                    foreach (var p in Process.GetProcessesByName("dnscrypt-proxy"))
                    {
                        try { p.Kill(); } catch { }
                    }
                    _isDoHActive = false;
                    ResetDnsToDhcpSync();
                }
                catch { }
            }

            CleanUpBypassServicesAndDrivers();
            
            // 2. Restore Game Mode services synchronously before process termination (Issue 4)
            if (_isGameModeEnabled)
            {
                try
                {
                    ToggleServices(true);
                    _isGameModeEnabled = false;
                }
                catch { }
            }

            // 3. Cleanup Instant Replay
            try
            {
                _replayDiskTimer?.Stop();
                _hotkeyManager?.Dispose();
                _ffmpegManager?.Dispose();
            }
            catch { }

            // 4. Cleanup Voice Translation
            try
            {
                if (_translatorProcess != null && !_translatorProcess.HasExited) _translatorProcess.Kill();
            }
            catch { }
            try
            {
                if (_reverseTranslatorProcess != null && !_reverseTranslatorProcess.HasExited) _reverseTranslatorProcess.Kill();
            }
            catch { }

            // 5. Cleanup LibreHardwareMonitor & Discord RPC
            try
            {
                _hwTimer?.Stop();
                _computer?.Close();
                _computer = null;
                _discordRpc?.Dispose();
                _discordRpc = null;
            }
            catch { }

            // 6. Dispose notify tray icon to prevent ghost icons (Issue 10)
            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null!;
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            PerformShutdownCleanup();
            base.OnClosed(e);
        }

        private async void ToggleBypass_Click(object? sender, RoutedEventArgs? e)
        {
            if (_isBypassActive)
            {
                // Stop WARP and purge driver services
                CleanUpBypassServicesAndDrivers();
                
                // Stop ProxiFyre and ciadpi
                try {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("ProxiFyre")) { p.Kill(); }
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("ciadpi")) { p.Kill(); }
                } catch { }

                _isBypassActive = false;
                UpdateDiscordPresence();
                BtnToggleBypass.Content = "⚡ WARP SPLIT TUNNELİ BAŞLAT";
                BtnToggleBypass.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254)); // #00F2FE
                BtnToggleBypass.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 19, 26));
                
                BypassStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 255, 8, 68)); // #33FF0844
                BypassStatusText.Text = "KAPALI";
                BypassStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                BypassStatusVal.Text = "Tüm ISS'ler ile %100 uyumludur.";
                
                var settings = PulseAppSettings.Load();
                settings.AutoBypassEnabled = false;
                settings.Save();

                DnsStatusVal.Text = "Şu anki: Otomatik (DHCP)";
                DnsStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                _ = PushWarpTelemetryAsync();
            }
            else
            {
                // Start WARP Split Tunnel
                BtnToggleBypass.IsEnabled = false;
                string lastErrorMsg = "";
                bool success = await _warpManager.StartWarpAsync((msg) =>
                {
                    lastErrorMsg = msg;
                    Dispatcher.Invoke(() => { BypassStatusVal.Text = msg; });
                });

                if (success)
                {
                    _isBypassActive = true;
                    UpdateDiscordPresence();
                    BtnToggleBypass.Content = "WARP TUNNELİ DURDUR";
                    BtnToggleBypass.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68)); // #FF0844
                    BtnToggleBypass.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250)); // #F8F9FA
                    
                    BypassStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 0, 255, 135)); // #3300FF87
                    BypassStatusText.Text = "WARP AKTİF";
                    BypassStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                    BypassStatusVal.Text = "Discord ve Roblox WARP tünelinden geçiyor (Oyunlar 0 Ping).";
                    
                    var settings = PulseAppSettings.Load();
                    settings.AutoBypassEnabled = true;
                    settings.AutoDnsMode = "cloudflare";
                    settings.Save();

                    DnsStatusVal.Text = "Şu anki: Cloudflare DNS (1.1.1.1 Otomatik)";
                    DnsStatusVal.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                    _ = PushWarpTelemetryAsync();
                }
                else
                {
                    BypassStatusVal.Text = string.IsNullOrEmpty(lastErrorMsg) ? "WARP Bağlantı Hatası!" : lastErrorMsg;
                }

                BtnToggleBypass.IsEnabled = true;
            }
        }

        private async Task ToggleBrowserVpnAsync()
        {
            if (_browserVpnLock.CurrentCount == 0) return;
            bool newState = !(ChkBrowserVpn.IsChecked == true);
            await SetBrowserVpnStateAsync(newState);
        }

        private async Task SetBrowserVpnStateAsync(bool isEnabled)
        {
            if (!await _browserVpnLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                if (ChkBrowserVpn != null) ChkBrowserVpn.IsEnabled = false;
                if (_itemBrowserVpn != null) _itemBrowserVpn.Enabled = false;

                if (ChkBrowserVpn != null) ChkBrowserVpn.IsChecked = isEnabled;
                _warpManager.IsBrowserVpnEnabled = isEnabled;
                UpdateBrowserVpnUI(isEnabled);

                var settings = PulseAppSettings.Load();
                settings.BrowserVpnEnabled = isEnabled;
                settings.Save();

                if (_isBypassActive)
                {
                    bool success = await _warpManager.SetBrowserVpnAsync(isEnabled, (msg) =>
                    {
                        Dispatcher.Invoke(() => { BypassStatusVal.Text = msg; });
                    });

                    if (!success)
                    {
                        Dispatcher.Invoke(() => { BypassStatusVal.Text = "Browser VPN uygulanamadı!"; });
                    }
                }

                if (_notifyIcon != null && (WindowState == WindowState.Minimized || Visibility != Visibility.Visible))
                {
                    _notifyIcon.ShowBalloonTip(2000, "AlpNet Pulse", isEnabled ? "🌐 Browser VPN: AÇIK" : "🌐 Browser VPN: KAPALI", WinForms.ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrowserVPN] Error: {ex.Message}");
            }
            finally
            {
                if (ChkBrowserVpn != null) ChkBrowserVpn.IsEnabled = true;
                if (_itemBrowserVpn != null) _itemBrowserVpn.Enabled = true;
                _browserVpnLock.Release();
            }
        }

        private async void ChkBrowserVpn_Click(object sender, RoutedEventArgs e)
        {
            bool targetState = ChkBrowserVpn.IsChecked == true;
            if (_browserVpnLock.CurrentCount == 0)
            {
                ChkBrowserVpn.IsChecked = !targetState;
                return;
            }
            await SetBrowserVpnStateAsync(targetState);
        }

        private void UpdateBrowserVpnUI(bool isEnabled)
        {
            if (_itemBrowserVpn != null)
            {
                _itemBrowserVpn.Checked = isEnabled;
                _itemBrowserVpn.Text = isEnabled ? "🌐 Browser VPN: AÇIK ✅" : "🌐 Browser VPN: KAPALI ❌";
            }

            if (BadgeBrowserVpn == null || TxtBrowserVpnStatus == null) return;

            if (isEnabled)
            {
                BadgeBrowserVpn.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 0, 242, 254)); // #3300F2FE
                TxtBrowserVpnStatus.Text = "AÇIK";
                TxtBrowserVpnStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
            }
            else
            {
                BadgeBrowserVpn.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 255, 8, 68)); // #33FF0844
                TxtBrowserVpnStatus.Text = "KAPALI";
                TxtBrowserVpnStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
            }
        }

        private void CmbBypassMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void ExtractBypassResources()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AlpBypass");
            if (!System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.CreateDirectory(tempDir);
            }

            try
            {
                string blacklistPath = System.IO.Path.Combine(tempDir, "dpi_blacklist.txt");
                System.IO.File.WriteAllText(blacklistPath, "discord.com\r\ndiscord.gg\r\ndiscordapp.com\r\ndiscordapp.net\r\ndiscord.media\r\ndiscord.dev\r\nx.com\r\ntwitter.com\r\nt.co\r\ntwimg.com\r\nroblox.com\r\nrbxcdn.com\r\nrobloxlabs.com\r\nrbx.com\r\n");
            }
            catch { }

            // WinDivert ve GoodbyeDPI, Easy Anti-Cheat ve BattlEye tarafından engellendiği için çıkarılmaz.
            // Bunun yerine eski kalıntılar temizlenir.
            string[] files = Array.Empty<string>();
            try
            {
                string[] badFiles = { "WinDivert64.sys", "WinDivert.dll", "WinDivert32.sys", "goodbyedpi.exe" };
                foreach (var bf in badFiles)
                {
                    var fp = System.IO.Path.Combine(tempDir, bf);
                    if (System.IO.File.Exists(fp)) { try { System.IO.File.Delete(fp); } catch { } }
                }
            }
            catch { }
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();

            foreach (var file in files)
            {
                string resName = "AlpNetPulse.Resources." + file;
                string outPath = System.IO.Path.Combine(tempDir, file);

                try
                {
                    using (System.IO.Stream? stream = assembly.GetManifestResourceStream(resName))
                    {
                        if (stream != null)
                        {
                            using (System.IO.FileStream fs = new System.IO.FileStream(outPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                            {
                                stream.CopyTo(fs);
                            }
                        }
                    }
                }
                catch (System.IO.IOException)
                {
                }
            }
        }
        #endregion

        #region Askeri Düzey DNS (DoH)
        private async Task EnsureDoHProxyDownloadedAsync()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AlpBypass");
            if (!System.IO.Directory.Exists(tempDir))
            {
                System.IO.Directory.CreateDirectory(tempDir);
            }

            string zipPath = System.IO.Path.Combine(tempDir, "dnscrypt.zip");
            string extractPath = System.IO.Path.Combine(tempDir, "dnscrypt");
            string exePath = System.IO.Path.Combine(extractPath, "win64", "dnscrypt-proxy.exe");
            string tomlPath = System.IO.Path.Combine(extractPath, "win64", "dnscrypt-proxy.toml");

            if (!System.IO.File.Exists(exePath))
            {
                foreach (var client in new[] { _httpClient })
                {
                    var response = await client.GetAsync("https://github.com/DNSCrypt/dnscrypt-proxy/releases/download/2.1.5/dnscrypt-proxy-win64-2.1.5.zip");
                    response.EnsureSuccessStatusCode();
                    using (var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }
                if (System.IO.Directory.Exists(extractPath)) System.IO.Directory.Delete(extractPath, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractPath);
            }

            string tomlContent = @"
server_names = ['cloudflare', 'google']
listen_addresses = ['127.0.0.1:53']
require_dnssec = true
require_nolog = true
require_nofilter = true
fallback_resolvers = ['9.9.9.9:53', '8.8.8.8:53']

[sources]
  [sources.public-resolvers]
    urls = ['https://raw.githubusercontent.com/DNSCrypt/dnscrypt-resolvers/master/v3/public-resolvers.md', 'https://download.dnscrypt.info/resolvers-list/v3/public-resolvers.md']
    cache_file = 'public-resolvers.md'
    minisign_key = 'RWQf6LRCGA9i53mlYecO4IzT51TGPpvWucNSCh1CBM0QTaLn73Y7GFO3'
";
            System.IO.File.WriteAllText(tomlPath, tomlContent);
        }

        private async void ToggleDoH_Click(object? sender, RoutedEventArgs? e)
        {
            if (_isDoHActive)
            {
                // Stop DoH
                if (_dohProcess != null && !_dohProcess.HasExited)
                {
                    try { _dohProcess.Kill(); } catch { }
                }
                foreach (var p in Process.GetProcessesByName("dnscrypt-proxy"))
                {
                    try { p.Kill(); } catch { }
                }

                _isDoHActive = false;
                BtnToggleDoH.Content = "🔒 ASKERİ DÜZEY ŞİFRELİ DNS BAŞLAT";
                BtnToggleDoH.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 19, 26)); // #11131A
                BtnToggleDoH.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135)); // #00FF87
                
                ChangeDns("dhcp", "", "Otomatik (DHCP)");
            }
            else
            {
                // Start DoH
                BtnToggleDoH.IsEnabled = false;

                try
                {
                    // Eski işlemleri temizle
                    if (_dohProcess != null && !_dohProcess.HasExited)
                    {
                        try { _dohProcess.Kill(); } catch { }
                    }
                    foreach (var p in Process.GetProcessesByName("dnscrypt-proxy"))
                    {
                        try { p.Kill(); } catch { }
                    }

                    BtnToggleDoH.Content = "İNDİRİLİYOR... LÜTFEN BEKLEYİN";
                    await EnsureDoHProxyDownloadedAsync();
                    BtnToggleDoH.Content = "BAŞLATILIYOR...";

                    await Task.Run(() =>
                    {
                        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AlpBypass");
                        string extractPath = System.IO.Path.Combine(tempDir, "dnscrypt");
                        string exePath = System.IO.Path.Combine(extractPath, "win64", "dnscrypt-proxy.exe");

                        var psi = new ProcessStartInfo(exePath, "-config dnscrypt-proxy.toml")
                        {
                            WorkingDirectory = System.IO.Path.Combine(extractPath, "win64"),
                            UseShellExecute = true,
                            Verb = "runas",
                            WindowStyle = ProcessWindowStyle.Hidden
                        };

                        _dohProcess = Process.Start(psi);
                        
                        // Kısa bir süre bekle ki proxy başlasın
                        System.Threading.Thread.Sleep(1000);
                    });

                    _isDoHActive = true;
                    BtnToggleDoH.Content = "🔓 ASKERİ DÜZEY DNS'İ DURDUR";
                    BtnToggleDoH.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68)); // #FF0844
                    BtnToggleDoH.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250)); // #F8F9FA
                    
                    ChangeDns("127.0.0.1", "127.0.0.1", "Askeri Düzey Gizlilik (DoH)");
                }
                finally
                {
                    BtnToggleDoH.IsEnabled = true;
                }
            }
        }
        #endregion

        #region Pro Tools

        private async void ScanPassword_Click(object sender, RoutedEventArgs e)
        {
            string password = PwnedPasswordInput.Password;
            if (string.IsNullOrWhiteSpace(password)) return;

            BtnScanPassword.IsEnabled = false;
            BtnScanPassword.Content = "TARANIYOR...";
            PwnedPasswordResult.Text = "Sunucuya bağlanılıyor (Anonim Hash)...";
            PwnedPasswordResult.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

            try
            {
                string hashStr = "";
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password));
                    hashStr = BitConverter.ToString(hashBytes).Replace("-", "").ToUpperInvariant();
                }

                string prefix = hashStr.Substring(0, 5);
                string suffix = hashStr.Substring(5);

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.pwnedpasswords.com/range/{prefix}");
                request.Headers.Add("User-Agent", "AlpNetPulse-DarkWebScanner");
                var res = await _httpClient.SendAsync(request);
                res.EnsureSuccessStatusCode();
                string response = await res.Content.ReadAsStringAsync();

                bool pwned = false;
                long count = 0;

                var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(':');
                    if (parts.Length == 2 && parts[0] == suffix)
                    {
                        pwned = true;
                        long.TryParse(parts[1], out count);
                        break;
                    }
                }

                if (pwned)
                {
                    PwnedPasswordResult.Text = $"TEHLİKE! Bu şifre daha önce {count} kez sızdırılmış!";
                    PwnedPasswordResult.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                }
                else
                {
                    PwnedPasswordResult.Text = "GÜVENLİ! Şifreniz hiçbir sızıntıda bulunamadı.";
                    PwnedPasswordResult.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                }
            }
            catch (Exception ex)
            {
                PwnedPasswordResult.Text = "Bağlantı hatası: " + ex.Message;
                PwnedPasswordResult.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 204, 0));
            }
            finally
            {
                BtnScanPassword.IsEnabled = true;
                BtnScanPassword.Content = "ŞİFREYİ TARAT";
                PwnedPasswordInput.Password = "";
            }
        }
        #endregion
        #region GAME MODE & HARDWARE MONITOR

        private void HwTimer_Tick(object? sender, EventArgs e)
        {
            if (PageGameMode.Visibility != Visibility.Visible || _computer == null || WindowState == WindowState.Minimized || !this.IsActive) return;

            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    // Skip GPU polling if Game Mode is active to prevent micro-stuttering/FPS drops in games
                    if (_isGameModeEnabled && (hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd))
                    {
                        continue;
                    }

                    hardware.Update();
                    
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        LblCpuName.Text = hardware.Name;
                        float totalLoad = 0;
                        bool foundLoad = false;

                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Load)
                            {
                                if (sensor.Name.Contains("Total"))
                                {
                                    totalLoad = sensor.Value ?? 0;
                                    foundLoad = true;
                                }
                                else if (!foundLoad) 
                                {
                                    totalLoad = Math.Max(totalLoad, sensor.Value ?? 0);
                                }
                            }
                        }

                        if (foundLoad || totalLoad > 0) 
                            LblCpuLoad.Text = $"{totalLoad:0} %";
                    }
                    else if (hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd)
                    {
                        LblGpuName.Text = hardware.Name;
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Name.Contains("Core"))
                                LblGpuTemp.Text = $"{sensor.Value:0.0} °C";
                                
                            if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Core"))
                                LblGpuLoad.Text = $"{sensor.Value:0} %";
                        }
                    }
                }
            }
            catch { }
        }

        public class GameProcessItem
        {
            public Process TargetProcess { get; set; } = null!;
            public int Id => TargetProcess.Id;
            public string ProcessName => TargetProcess.ProcessName;
            public string DisplayName { get; set; } = string.Empty;
        }

        private void RefreshGamesList_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                var processes = Process.GetProcesses()
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                    .OrderBy(p => p.ProcessName)
                    .Select(p => new GameProcessItem 
                    { 
                        TargetProcess = p, 
                        DisplayName = $"{p.MainWindowTitle} ({p.ProcessName})"
                    })
                    .ToList();
                    
                CmbRunningGames.ItemsSource = processes;
                if (processes.Count > 0) CmbRunningGames.SelectedIndex = 0;
            }
            catch { }
        }

        private void OptimizePriority_Click(object sender, RoutedEventArgs e)
        {
            if (CmbRunningGames.SelectedItem is GameProcessItem item)
            {
                var p = item.TargetProcess;
                try
                {
                    p.PriorityClass = ProcessPriorityClass.AboveNormal;
                    LblGameOptimizationStatus.Text = $"{p.ProcessName} işlemi için SİSTEM önceliği NORMAL ÜSTÜ (Above Normal) olarak ayarlandı.";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("Öncelik ayarlanamadı. Uygulamayı yönetici olarak çalıştırmayı deneyin.\n\nHata: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OptimizeCores_Click(object sender, RoutedEventArgs e)
        {
            if (CmbRunningGames.SelectedItem is GameProcessItem item)
            {
                var p = item.TargetProcess;
                try
                {
                    // Tüm mantıksal ve fiziksel çekirdeklerin (P-Core/E-Core/HT) tam kapasite çalışmasını sağla
                    long allCoresMask = (1L << Environment.ProcessorCount) - 1;
                    p.ProcessorAffinity = (IntPtr)allCoresMask;
                    p.PriorityClass = ProcessPriorityClass.High;
                    LblGameOptimizationStatus.Text = $"{p.ProcessName} tüm CPU çekirdekleri (Tüm Çekirdekler & YÜKSEK Öncelik) için tam performans ayarlandı.";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("ÇEKİRDEK optimizasyonu başarısız. Uygulamayı yönetici olarak çalıştırmayı deneyin.\n\nHata: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RevertOptimization_Click(object sender, RoutedEventArgs e)
        {
            if (CmbRunningGames.SelectedItem is GameProcessItem item)
            {
                var p = item.TargetProcess;
                try
                {
                    // Önceliği normale döndür
                    p.PriorityClass = ProcessPriorityClass.Normal;

                    // Tüm çekirdekleri (HT/SMT dahil) tekrar etkinleştir
                    long allCoresMask = (1L << Environment.ProcessorCount) - 1;
                    p.ProcessorAffinity = (IntPtr)allCoresMask;

                    LblGameOptimizationStatus.Text = $"{p.ProcessName} işlemi varsayılan (normal) ayarlara döndürüldü.";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("Ayarlar sıfırlanamadı. Uygulamayı yönetici olarak çalıştırmayı deneyin.\n\nHata: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region INSTANT REPLAY SYSTEM

        private async Task InitializeReplaySystemAsync()
        {
            try
            {
                // Load settings
                _replaySettings = ReplaySettings.Load();
                ApplyReplaySettingsToUI();

                // Initialize FFmpeg Manager
                _ffmpegManager = new FFmpegManager();
                _ffmpegManager.StatusChanged += (msg) => Dispatcher.Invoke(() => TxtReplayStatus.Text = msg);
                _ffmpegManager.RecordingStateChanged += (isRecording) => Dispatcher.Invoke(() => UpdateReplayUI(isRecording));
                _ffmpegManager.ClipSaved += (path) => Dispatcher.Invoke(() =>
                {
                    RefreshClipsList();
                    if (_notifyIcon != null)
                        _notifyIcon.ShowBalloonTip(3000, "AlpNet Pulse", $"Klip kaydedildi!", WinForms.ToolTipIcon.Info);
                });
                _ffmpegManager.ErrorOccurred += (err) => Dispatcher.Invoke(() =>
                {
                    TxtReplayStatus.Text = err;
                    TxtReplayStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                });

                bool ffmpegReady = await _ffmpegManager.InitializeAsync(_replaySettings.FfmpegPath);
                if (ffmpegReady)
                {
                    TxtReplayEncoder.Text = _ffmpegManager.DetectedEncoder switch
                    {
                        "h264_nvenc" => "NVIDIA NVENC H.264",
                        "h264_amf" => "AMD AMF H.264",
                        "h264_qsv" => "Intel QSV H.264",
                        "libx264" => "Yazılım (x264) - CPU",
                        _ => _ffmpegManager.DetectedEncoder
                    };
                }

                // Initialize Global Hotkey Manager
                _hotkeyManager = new GlobalHotkeyManager(_replaySettings.HotkeyVirtualKey, _replaySettings.HotkeyModifiers);
                _hotkeyManager.HotkeyPressed += () => Dispatcher.Invoke(async () =>
                {
                    if (_ffmpegManager?.IsRecording == true)
                    {
                        await _ffmpegManager.SaveClipAsync(_replaySettings.SaveFolder);
                    }
                });
                _hotkeyManager.KeyCaptured += (vk, mods, name) => Dispatcher.Invoke(() =>
                {
                    _replaySettings.HotkeyVirtualKey = vk;
                    _replaySettings.HotkeyModifiers = mods;
                    _replaySettings.HotkeyDisplay = name;
                    _replaySettings.Save();
                    _hotkeyManager.SetHotkey(vk, mods);
                    BtnSetHotkey.Content = name;
                    BtnSetHotkey.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 23, 30));
                });
                _hotkeyManager.Start();

                // Start disk usage update timer
                _replayDiskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _replayDiskTimer.Tick += (s, ev) =>
                {
                    if (_ffmpegManager != null && _ffmpegManager.IsRecording)
                    {
                        double mb = _ffmpegManager.GetSegmentDiskUsageMB();
                        TxtReplayDisk.Text = $"{mb:0.0} MB ({_ffmpegManager.GetSegmentCount()} segment)";
                    }
                };
                _replayDiskTimer.Start();

                // Load saved clips list
                RefreshClipsList();

                // Start watching clips folder
                string folder = _replaySettings.SaveFolder;
                if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
                {
                    folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AlpNet Pulse");
                    if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
                }

                if (_clipsWatcher != null)
                {
                    _clipsWatcher.Dispose();
                }

                _clipsWatcher = new System.IO.FileSystemWatcher(folder, "*.mp4")
                {
                    EnableRaisingEvents = true,
                    NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.LastWrite
                };
                
                _clipsWatcher.Created += (s, e) => Dispatcher.InvokeAsync(RefreshClipsList);
                _clipsWatcher.Deleted += (s, e) => Dispatcher.InvokeAsync(RefreshClipsList);
                _clipsWatcher.Renamed += (s, e) => Dispatcher.InvokeAsync(RefreshClipsList);
            }
            catch (Exception ex)
            {
                TxtReplayStatus.Text = $"Replay sistemi başlatılamadı: {ex.Message}";
            }
        }

        private void ApplyReplaySettingsToUI()
        {
            // Buffer buttons
            ResetBufferButtons();
            var activeBufferBtn = _replaySettings.BufferMinutes switch
            {
                1 => BtnBuffer1,
                3 => BtnBuffer3,
                5 => BtnBuffer5,
                10 => BtnBuffer10,
                30 => BtnBuffer30,
                _ => BtnBuffer10
            };
            activeBufferBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
            activeBufferBtn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));

            // FPS buttons
            if (_replaySettings.Fps == 60)
            {
                BtnFps30.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
                BtnFps30.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                BtnFps60.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
                BtnFps60.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));
            }

            // Quality buttons
            ResetQualityButtons();
            var activeQualityBtn = _replaySettings.Quality switch
            {
                "Low" => BtnQualityLow,
                "Medium" => BtnQualityMedium,
                "High" => BtnQualityHigh,
                _ => BtnQualityMedium
            };
            activeQualityBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
            activeQualityBtn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));

            // Hotkey
            BtnSetHotkey.Content = _replaySettings.HotkeyDisplay;

            // Save folder
            TxtReplaySaveFolder.Text = _replaySettings.SaveFolder;

            // Update estimate
            UpdateDiskEstimate();
            
            PopulateAudioDevices();
        }

        private void UpdateReplayUI(bool isRecording)
        {
            BtnStartReplay.IsEnabled = !isRecording;
            BtnStopReplay.IsEnabled = isRecording;

            if (isRecording)
            {
                BtnStartReplay.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
                BtnStartReplay.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                
                BtnStopReplay.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                BtnStopReplay.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250));

                ReplayStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x00, 0xFF, 0x87));
                ReplayStatusBadgeText.Text = "KAYIT AKTİF";
                ReplayStatusBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                TxtReplayStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
            }
            else
            {
                BtnStartReplay.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 126, 234));
                BtnStartReplay.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250));

                BtnStopReplay.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
                BtnStopReplay.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                ReplayStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0x08, 0x44));
                ReplayStatusBadgeText.Text = "KAPALI";
                ReplayStatusBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
                TxtReplayStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                TxtReplayDisk.Text = "0 MB";
            }
        }

        private async void StartReplay_Click(object sender, RoutedEventArgs e)
        {
            if (_ffmpegManager == null) return;

            BtnStartReplay.IsEnabled = false;
            BtnStartReplay.Content = "⏳ Başlatılıyor...";

            try
            {
                await Task.Run(() =>
                {
                    _ffmpegManager.StartRecording(
                        _replaySettings.BufferMinutes,
                        _replaySettings.Fps,
                        _replaySettings.GetBitrate(),
                        _replaySettings.RecordAudio,
                        _replaySettings.RecordMic,
                        _replaySettings.MicDeviceName);
                });
            }
            catch (Exception ex)
            {
                TxtReplayStatus.Text = $"Kayıt Hatası: {ex.Message}";
                TxtReplayStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68));
            }
            finally
            {
                BtnStartReplay.Content = "🔴 KAYDI BAŞLAT";
            }
        }

        private void StopReplay_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _ffmpegManager?.StopRecording(false);
            }
            catch { }
        }

        private void PopulateAudioDevices()
        {
            try
            {
                CmbMicDevices.Items.Clear();
                CmbMicDevices.Items.Add("Varsayılan Mikrofon");
                
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    var capabilities = WaveIn.GetCapabilities(i);
                    CmbMicDevices.Items.Add(capabilities.ProductName);
                }

                if (!string.IsNullOrEmpty(_replaySettings.MicDeviceName) && CmbMicDevices.Items.Contains(_replaySettings.MicDeviceName))
                {
                    CmbMicDevices.SelectedItem = _replaySettings.MicDeviceName;
                }
                else
                {
                    CmbMicDevices.SelectedIndex = 0;
                }

                ChkRecordAudio.IsChecked = _replaySettings.RecordAudio;
                ChkRecordMic.IsChecked = _replaySettings.RecordMic;
            }
            catch { }
        }

        private void AudioSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkRecordAudio == null || ChkRecordMic == null) return;
            
            _replaySettings.RecordAudio = ChkRecordAudio.IsChecked ?? true;
            _replaySettings.RecordMic = ChkRecordMic.IsChecked ?? true;
            _replaySettings.Save();

            _ffmpegManager?.UpdateAudioSettings(_replaySettings.RecordAudio, _replaySettings.RecordMic, _replaySettings.MicDeviceName);
        }

        private void MicDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CmbMicDevices.SelectedItem != null)
            {
                _replaySettings.MicDeviceName = CmbMicDevices.SelectedItem.ToString() ?? string.Empty;
                _replaySettings.Save();

                _ffmpegManager?.UpdateAudioSettings(_replaySettings.RecordAudio, _replaySettings.RecordMic, _replaySettings.MicDeviceName);
            }
        }

        private void BufferDuration_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int minutes))
            {
                ResetBufferButtons();
                btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
                btn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));

                _replaySettings.BufferMinutes = minutes;
                _replaySettings.Save();

                _ffmpegManager?.SetBufferMinutes(minutes);
                UpdateDiskEstimate();
            }
        }

        private void FpsSetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int fps))
            {
                BtnFps30.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
                BtnFps30.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
                BtnFps60.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
                BtnFps60.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

                btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
                btn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));

                _replaySettings.Fps = fps;
                _replaySettings.Save();
                UpdateDiskEstimate();
            }
        }

        private void QualitySetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string quality)
            {
                ResetQualityButtons();
                btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
                btn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));

                _replaySettings.Quality = quality;
                _replaySettings.Save();
                UpdateDiskEstimate();
            }
        }

        private void SetHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (_hotkeyManager == null) return;

            BtnSetHotkey.Content = "Bir tuşa basın...";
            BtnSetHotkey.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 126, 234));
            _hotkeyManager.StartCapturingKey();
        }

        private void SelectReplayFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Kliplerin kaydedileceği klasörü seçin",
                SelectedPath = _replaySettings.SaveFolder
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _replaySettings.SaveFolder = dialog.SelectedPath;
                _replaySettings.Save();
                TxtReplaySaveFolder.Text = dialog.SelectedPath;
            }
        }

        private void OpenReplayFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(_replaySettings.SaveFolder))
                    Directory.CreateDirectory(_replaySettings.SaveFolder);

                Process.Start(new ProcessStartInfo
                {
                    FileName = _replaySettings.SaveFolder,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void PlayClip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string filePath)
            {
                OpenVideoPlayer(filePath);
            }
        }

        private void OpenVideoPlayer(string filePath)
        {
            try
            {
                if (System.IO.File.Exists(filePath))
                {
                    string ffmpegPath = _replaySettings.FfmpegPath;
                    if (string.IsNullOrEmpty(ffmpegPath))
                    {
                        ffmpegPath = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "AlpNet", "ffmpeg", "ffmpeg.exe");
                    }

                    var playerWindow = new VideoPlayerWindow(filePath, ffmpegPath);
                    playerWindow.Owner = this;
                    playerWindow.ShowDialog();
                    
                    RefreshClipsList();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Oynatıcı açılamadı: {ex.Message}");
            }
        }

        private void RenameBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn)
            {
                if (btn.Parent is Grid grid && grid.Children[0] is StackPanel panel)
                {
                    var textBlock = panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "TxtFileName");
                    var textBox = panel.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
                    if (textBlock != null && textBox != null)
                    {
                        textBlock.Visibility = Visibility.Collapsed;
                        textBox.Visibility = Visibility.Visible;
                        string fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(textBox.Text);
                        textBox.Text = fileNameWithoutExt;
                        textBox.Focus();
                        textBox.SelectAll();
                    }
                }
            }
        }

        private void EditFileName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox textBox)
            {
                RenameClip(textBox);
            }
            else if (e.Key == System.Windows.Input.Key.Escape && sender is System.Windows.Controls.TextBox tb)
            {
                CancelRename(tb);
            }
        }

        private void EditFileName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox textBox)
            {
                RenameClip(textBox);
            }
        }

        private void CancelRename(System.Windows.Controls.TextBox textBox)
        {
            if (textBox.Parent is StackPanel panel)
            {
                var textBlock = panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "TxtFileName");
                if (textBlock != null)
                {
                    textBox.Visibility = Visibility.Collapsed;
                    textBlock.Visibility = Visibility.Visible;
                }
            }
        }

        private void RenameClip(System.Windows.Controls.TextBox textBox)
        {
            if (textBox.Visibility == Visibility.Collapsed) return;

            string oldFullPath = textBox.Tag as string ?? "";
            string newName = textBox.Text.Trim();

            if (string.IsNullOrEmpty(newName) || !System.IO.File.Exists(oldFullPath))
            {
                CancelRename(textBox);
                return;
            }

            try
            {
                string dir = System.IO.Path.GetDirectoryName(oldFullPath) ?? "";
                string ext = System.IO.Path.GetExtension(oldFullPath);
                string newFullPath = System.IO.Path.Combine(dir, newName + ext);

                if (oldFullPath.Equals(newFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    CancelRename(textBox);
                    return;
                }

                if (System.IO.File.Exists(newFullPath))
                {
                    System.Windows.MessageBox.Show("Bu isimde bir dosya zaten var.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                    CancelRename(textBox);
                    return;
                }

                System.IO.File.Move(oldFullPath, newFullPath);
                RefreshClipsList();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"İsim değiştirilirken hata oluştu: {ex.Message}");
                CancelRename(textBox);
            }
        }

        private System.IO.FileSystemWatcher? _clipsWatcher;

        private void DeleteClip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.Tag is string fullPath)
            {
                try
                {
                    if (System.IO.File.Exists(fullPath))
                    {
                        var result = System.Windows.MessageBox.Show("Bu klibi silmek istediğinize emin misiniz?", "Klibi Sil", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                        if (result == System.Windows.MessageBoxResult.Yes)
                        {
                            System.IO.File.Delete(fullPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Klip silinirken hata oluştu: {ex.Message}");
                }
            }
        }

        private void RefreshClipsList()
        {
            if (_ffmpegManager == null) return;

            var clips = _ffmpegManager.GetSavedClips(_replaySettings.SaveFolder);
            ReplayClipsList.ItemsSource = clips;
            TxtNoClips.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ResetBufferButtons()
        {
            var defaultBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
            var defaultFg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
            BtnBuffer1.Background = defaultBg; BtnBuffer1.Foreground = defaultFg;
            BtnBuffer3.Background = defaultBg; BtnBuffer3.Foreground = defaultFg;
            BtnBuffer5.Background = defaultBg; BtnBuffer5.Foreground = defaultFg;
            BtnBuffer10.Background = defaultBg; BtnBuffer10.Foreground = defaultFg;
            BtnBuffer30.Background = defaultBg; BtnBuffer30.Foreground = defaultFg;
        }

        private void ResetQualityButtons()
        {
            var defaultBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
            var defaultFg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
            BtnQualityLow.Background = defaultBg; BtnQualityLow.Foreground = defaultFg;
            BtnQualityMedium.Background = defaultBg; BtnQualityMedium.Foreground = defaultFg;
            BtnQualityHigh.Background = defaultBg; BtnQualityHigh.Foreground = defaultFg;
        }

        private void UpdateDiskEstimate()
        {
            int estimateMB = _replaySettings.GetEstimatedDiskUsageMB();
            string qualityTr = _replaySettings.Quality switch
            {
                "Low" => "Düşük",
                "Medium" => "Orta",
                "High" => "Yüksek",
                _ => "Orta"
            };
            TxtReplayEstimate.Text = $"Tahmini disk kullanımı: ~{estimateMB} MB ({_replaySettings.BufferMinutes} dk, {qualityTr} kalite, {_replaySettings.Fps} FPS)";
        }

        #endregion

    }

    #region Helper Classes
    public class ProgressStreamContent : HttpContent
    {
        private readonly byte[] _content;
        private readonly Action<long> _onProgress;

        public ProgressStreamContent(byte[] content, Action<long> onProgress)
        {
            _content = content;
            _onProgress = onProgress;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            int bufferSize = 81920;
            long uploaded = 0;
            for (int i = 0; i < _content.Length; i += bufferSize)
            {
                int length = Math.Min(bufferSize, _content.Length - i);
                await stream.WriteAsync(_content, i, length);
                await stream.FlushAsync();
                uploaded += length;
                _onProgress?.Invoke(uploaded);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Length;
            return true;
        }
    }

    public class LicenseState
    {
        [JsonPropertyName("licenseKey")]
        public string LicenseKey { get; set; } = "";

        [JsonPropertyName("expiresAt")]
        public string ExpiresAt { get; set; } = "";

        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = "";

        [JsonIgnore]
        public bool Licensed { get; set; } = false;
    }

    public class AppState
    {
        public bool maintenance { get; set; }
        public string? maintenanceMsg { get; set; }
        public string? broadcastMsg { get; set; }
        public string? version { get; set; }
        public string? updateUrl { get; set; }
    }

    public class VerifyResponse
    {
        [JsonPropertyName("valid")]
        public bool Valid { get; set; }

        [JsonPropertyName("expiresAt")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("timeLeftMs")]
        public long TimeLeftMs { get; set; }
    }

    public class ActivateResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("expiresAt")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("durationDays")]
        public int DurationDays { get; set; }
    }

    public class ErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = "";
    }

    public class GeoIpInfo
    {
        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("org")]
        public string? Org { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("country_name")]
        public string? CountryName { get; set; }
    }

    public class AdapterUiItem
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
        public System.Windows.Media.Brush StatusBg { get; set; } = System.Windows.Media.Brushes.Transparent;
        public System.Windows.Media.Brush StatusFg { get; set; } = System.Windows.Media.Brushes.White;
        public int SortOrder { get; set; } = 2;
        public string? ToolTipText { get; set; }
    }


    #endregion
}

namespace AlpNetPulse { public class DirectMessage { public string message { get; set; } = ""; } }











