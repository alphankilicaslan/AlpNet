using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AlpNetPulse
{
    /// <summary>
    /// AlpTogether ekosistemi ile 100% uyumlu AlpNet Pulse telemetri ve hata/teşhis log servisi.
    /// AlpTogether Admin paneline (Telemetri, Olaylar, Arıza Kayıtları) anında düşer.
    /// </summary>
    public class AlpNetTelemetryService
    {
        public static AlpNetTelemetryService Instance { get; } = new();

        private const string FIREBASE_BASE_URL = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app";
        private const string FIREBASE_AUTH_SECRET = "ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
        public const string APP_ID = "alpnet-pulse";
        public const string APP_NAME = "AlpNet Pulse";
        public const string APP_VERSION = "2.2.9";

        private readonly HttpClient _http;
        private readonly string _logsDir;
        private readonly string _crashesDir;
        private readonly string _diagnosticsDir;
        private readonly string _mainLogFile;

        private readonly ConcurrentQueue<string> _recentLogs = new();
        private const int MAX_LOG_BUFFER = 250;
        private readonly object _fileLock = new();

        public string DeviceId { get; set; } = string.Empty;

        private AlpNetTelemetryService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            _http.DefaultRequestHeaders.Add("User-Agent", $"AlpNetPulse/{APP_VERSION}");

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string baseDir = Path.Combine(localAppData, "AlpNet");
            _logsDir = Path.Combine(baseDir, "logs");
            _crashesDir = Path.Combine(baseDir, "crashes");
            _diagnosticsDir = Path.Combine(baseDir, "diagnostics");
            _mainLogFile = Path.Combine(_logsDir, "alpnet_pulse.log");

            try
            {
                if (!Directory.Exists(_logsDir)) Directory.CreateDirectory(_logsDir);
                if (!Directory.Exists(_crashesDir)) Directory.CreateDirectory(_crashesDir);
                if (!Directory.Exists(_diagnosticsDir)) Directory.CreateDirectory(_diagnosticsDir);
            }
            catch { }

            // İlk HWID belirleme
            EnsureDeviceId();

            Log("BAŞLATMA", $"AlpNet Pulse v{APP_VERSION} Telemetri Servisi başlatıldı. Cihaz: {DeviceId} ({Environment.MachineName})");
        }

        public string EnsureDeviceId()
        {
            if (!string.IsNullOrEmpty(DeviceId))
                return DeviceId;

            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var guid = key?.GetValue("MachineGuid")?.ToString();
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    DeviceId = ComputeMd5(guid.Trim());
                    return DeviceId;
                }
            }
            catch { }

            try
            {
                var macs = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback && ni.OperationalStatus == OperationalStatus.Up)
                    .Select(ni => ni.GetPhysicalAddress().ToString())
                    .Where(m => !string.IsNullOrEmpty(m) && m != "000000000000")
                    .ToList();
                if (macs.Count > 0)
                {
                    DeviceId = ComputeMd5(macs[0] + Environment.MachineName);
                    return DeviceId;
                }
            }
            catch { }

            DeviceId = ComputeMd5(Environment.MachineName + Environment.UserName);
            return DeviceId;
        }

        private static string ComputeMd5(string input)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            foreach (byte b in bytes)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        #region In-Memory & File Logging
        /// <summary>
        /// AlpNet içinde gerçekleşen tüm olayları, WARP hareketlerini ve durumları kaydeder.
        /// </summary>
        public void Log(string category, string message, string level = "INFO")
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{category}] {message}";

            _recentLogs.Enqueue(line);
            while (_recentLogs.Count > MAX_LOG_BUFFER)
            {
                _recentLogs.TryDequeue(out _);
            }

            Debug.WriteLine(line);

            // Yerel dosyaya yaz
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_mainLogFile, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        public void Info(string category, string message) => Log(category, message, "INFO");
        public void Warn(string category, string message) => Log(category, message, "WARN");
        public void Error(string category, string message) => Log(category, message, "ERROR");
        public void Success(string category, string message) => Log(category, message, "SUCCESS");

        public string GetRecentLogsText(int count = 60)
        {
            return string.Join(Environment.NewLine, _recentLogs.TakeLast(count));
        }

        public string GetLogsDirectory() => _logsDir;
        #endregion

        #region Firebase Helpers
        private static string BuildFirebaseUrl(string relativePath)
        {
            string clean = relativePath.TrimStart('/');
            return $"{FIREBASE_BASE_URL}/{clean}?auth={Uri.EscapeDataString(FIREBASE_AUTH_SECRET)}";
        }
        #endregion

        #region Error & Crash Telemetry (AlpTogether AutoErrorReport)
        /// <summary>
        /// Uygulama çökmesi veya kritik bir hata gerçekleştiğinde AlpTogether Admin paneline anında iletir.
        /// </summary>
        public async Task<bool> ReportErrorImmediatelyAsync(
            string errorSource,
            string errorMessage,
            string stackTrace = "",
            string? customDeviceId = null)
        {
            string hwid = !string.IsNullOrWhiteSpace(customDeviceId) ? customDeviceId : EnsureDeviceId();
            string reportId = "ERR-" + DateTime.Now.ToString("MMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var report = new
            {
                ReportId = reportId,
                Timestamp = timestamp,
                DeviceId = hwid,
                MachineName = Environment.MachineName,
                OsVersion = Environment.OSVersion.ToString(),
                AppId = APP_ID,
                AppName = APP_NAME,
                AppVersion = APP_VERSION,
                ErrorSource = errorSource,
                ErrorMessage = errorMessage,
                StackTrace = stackTrace,
                RecentLogs = GetRecentLogsText(40)
            };

            Log("HATA", $"Kritik hata raporu hazırlandı ({reportId}): {errorMessage}", "ERROR");

            // 1. Yerel olarak kaydet
            try
            {
                string localFile = Path.Combine(_crashesDir, $"{reportId}.json");
                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(localFile, json, Encoding.UTF8);
            }
            catch { }

            bool cloudSuccess = false;

            // 2. Firebase /alptogether/telemetry/errors/{reportId}.json
            try
            {
                string fbUrl = BuildFirebaseUrl($"alptogether/telemetry/errors/{reportId}.json");
                string jsonPayload = JsonSerializer.Serialize(report);
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var resp = await _http.PutAsync(fbUrl, content);
                cloudSuccess = resp.IsSuccessStatusCode;
            }
            catch { }

            // 3. Firebase /alptogether/telemetry/events/{eventId}.json
            try
            {
                string eventId = "EVT-" + DateTime.Now.ToString("MMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
                var evt = new
                {
                    EventId = eventId,
                    Timestamp = timestamp,
                    DeviceId = hwid,
                    MachineName = Environment.MachineName,
                    UserName = Environment.UserName,
                    EventType = "ERROR",
                    AppId = APP_ID,
                    AppName = APP_NAME,
                    AppVersion = APP_VERSION,
                    Details = errorMessage.Length > 200 ? errorMessage[..200] + "..." : errorMessage,
                    ExtraInfo = $"Kaynak: {errorSource} | {stackTrace}"
                };

                string fbEvtUrl = BuildFirebaseUrl($"alptogether/telemetry/events/{eventId}.json");
                string evtPayload = JsonSerializer.Serialize(evt);
                using var evtContent = new StringContent(evtPayload, Encoding.UTF8, "application/json");
                await _http.PutAsync(fbEvtUrl, evtContent);
            }
            catch { }

            return cloudSuccess;
        }
        #endregion

        #region User Diagnostics & Manual Log Submission ("Log Gönder")
        /// <summary>
        /// Kullanıcı Lisans ve Hesap sekmesinden "Log Gönder / Sorun Bildir" butonuna bastığında
        /// derinlemesine ağ ve sistem teşhisi toplayıp AlpTogether paneline gönderir.
        /// </summary>
        public async Task<(bool Success, string ReportId, string Message)> SendDiagnosticsAndLogsAsync(
            string? customDeviceId = null,
            string? licenseStatus = null,
            string? licenseExpiry = null,
            string? userNote = null)
        {
            string hwid = !string.IsNullOrWhiteSpace(customDeviceId) ? customDeviceId : EnsureDeviceId();
            string reportId = "DIAG-" + DateTime.Now.ToString("MMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            string eventId = "EVT-" + DateTime.Now.ToString("MMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            string ticketId = "TKT-" + DateTime.Now.ToString("MMddHHmm") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            Log("TEŞHİS", $"Kullanıcı teşhis log gönderimi başlattı ({reportId})...");

            // 1. Kapsamlı Sistem & Ağ Teşhis Raporu Derle
            var diagBuilder = new StringBuilder();
            diagBuilder.AppendLine("==================================================");
            diagBuilder.AppendLine($"  ALPNET PULSE SİSTEM VE BAĞLANTI TEŞHİS RAPORU");
            diagBuilder.AppendLine($"  Rapor ID : {reportId}");
            diagBuilder.AppendLine($"  Tarih    : {timestamp}");
            diagBuilder.AppendLine("==================================================");
            diagBuilder.AppendLine();

            // A. Donanım & Sistem
            diagBuilder.AppendLine("[SİSTEM BİLGİLERİ]");
            diagBuilder.AppendLine($"• Cihaz Kimliği (HWID) : {hwid}");
            diagBuilder.AppendLine($"• Bilgisayar Adı       : {Environment.MachineName}");
            diagBuilder.AppendLine($"• Oturum Kullanıcısı   : {Environment.UserName}");
            diagBuilder.AppendLine($"• İşletim Sistemi      : {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
            diagBuilder.AppendLine($"• .NET Çalışma Zamanı  : {Environment.Version}");
            diagBuilder.AppendLine($"• Mantıksal Çekirdek   : {Environment.ProcessorCount}");
            diagBuilder.AppendLine($"• Yönetici İzni (Admin): {IsAdministrator()}");
            diagBuilder.AppendLine($"• Sistem Çalışma Süresi: {TimeSpan.FromMilliseconds(Environment.TickCount64):d'g 'hh's 'mm'd'}");
            diagBuilder.AppendLine();

            // B. AlpNet Durumu
            diagBuilder.AppendLine("[ALPNET PULSE DURUMU]");
            diagBuilder.AppendLine($"• Uygulama Sürümü      : v{APP_VERSION}");
            diagBuilder.AppendLine($"• Lisans Durumu        : {licenseStatus ?? "Bilinmiyor"}");
            diagBuilder.AppendLine($"• Lisans Bitiş         : {licenseExpiry ?? "Bilinmiyor"}");
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "Bilinmiyor";
            diagBuilder.AppendLine($"• Çalışan Konum        : {exePath}");
            diagBuilder.AppendLine();

            // C. WireSock & WARP Servis Durumu
            diagBuilder.AppendLine("[WIRESOCK / WARP TEŞHİSİ]");
            bool wsProcRunning = Process.GetProcessesByName("wiresock-client").Length > 0 || Process.GetProcessesByName("wiresock").Length > 0;
            diagBuilder.AppendLine($"• WireSock Process     : {(wsProcRunning ? "ÇALIŞIYOR" : "ÇALIŞMIYOR")}");
            bool wsServiceRunning = WarpManager.IsWireSockServiceRunning();
            diagBuilder.AppendLine($"• WireSock Service     : {(wsServiceRunning ? "AKTİF / RUNNING" : "DURDURULDU VEYA YOK")}");

            string? wsBinaryPath = WarpManager.FindWireSockPath();
            diagBuilder.AppendLine($"• WireSock Binary      : {(string.IsNullOrEmpty(wsBinaryPath) ? "YOK (Bulunamadı)" : wsBinaryPath)}");

            string resDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res");
            string wgcfExe = Path.Combine(resDir, "wgcf.exe");
            string wgcfProfile = Path.Combine(resDir, "wgcf-profile.conf");
            if (!File.Exists(wgcfProfile))
            {
                string appDataProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet", "res", "wgcf-profile.conf");
                if (File.Exists(appDataProfile)) wgcfProfile = appDataProfile;
            }

            diagBuilder.AppendLine($"• res/wgcf.exe         : {(File.Exists(wgcfExe) ? "Mevcut (" + new FileInfo(wgcfExe).Length + " B)" : "YOK")}");
            diagBuilder.AppendLine($"• wgcf-profile.conf    : {(File.Exists(wgcfProfile) ? "Mevcut" : "YOK")}");

            // WARP profil detayları
            if (File.Exists(wgcfProfile))
            {
                try
                {
                    string profileText = File.ReadAllText(wgcfProfile);
                    string endpoint = System.Text.RegularExpressions.Regex.Match(profileText, @"Endpoint\s*=\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                    string mtu = System.Text.RegularExpressions.Regex.Match(profileText, @"MTU\s*=\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                    string dns = System.Text.RegularExpressions.Regex.Match(profileText, @"DNS\s*=\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                    diagBuilder.AppendLine($"  -> Profil Endpoint   : {endpoint}");
                    diagBuilder.AppendLine($"  -> Profil MTU        : {mtu}");
                    diagBuilder.AppendLine($"  -> Profil Tünel DNS  : {(string.IsNullOrEmpty(dns) ? "YOK (⚠️ ISS DNS Zehirlemesine Açık!)" : dns + " (Korumalı)")}");
                }
                catch { }
            }
            diagBuilder.AppendLine();

            // D. Ağ Adaptörleri ve IP Durumu
            diagBuilder.AppendLine("[AĞ ADAPTÖRLERİ]");
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 && ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                        continue;

                    var ipProps = ni.GetIPProperties();
                    var unicast = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    var gateway = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    var dnsList = ipProps.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList();

                    diagBuilder.AppendLine($"• Adaptör: {ni.Name} ({ni.Description})");
                    diagBuilder.AppendLine($"  - Durum: {ni.OperationalStatus} | Tip: {ni.NetworkInterfaceType} | Hız: {ni.Speed / 1_000_000} Mbps");
                    if (unicast != null) diagBuilder.AppendLine($"  - IPv4 : {unicast.Address}");
                    if (gateway != null) diagBuilder.AppendLine($"  - GW   : {gateway.Address}");
                    if (dnsList.Count > 0) diagBuilder.AppendLine($"  - DNS  : {string.Join(", ", dnsList)}");
                }
            }
            catch (Exception ex)
            {
                diagBuilder.AppendLine($"  - Adaptör bilgisi alınamadı: {ex.Message}");
            }
            diagBuilder.AppendLine();

            // E. Ağ & DNS Bağlantı Testi (Ping & Socket)
            diagBuilder.AppendLine("[BAĞLANTI & DNS TESTİ]");
            string cfDnsTest = await TestTcpPortAsync("1.1.1.1", 53, 1500);
            diagBuilder.AppendLine($"• Cloudflare DNS (1.1.1.1:53)       : {cfDnsTest}");
            string warpTest = await TestTcpPortAsync("162.159.192.1", 500, 1500);
            diagBuilder.AppendLine($"• Cloudflare WARP IP (162.159.192.1:500): {warpTest}");
            string discordDns = ResolveHost("discord.com");
            if (discordDns.Contains("195.175.")) discordDns += " ⚠️ (BTK/ISS Engelleme IP'si - Zehirlenmiş DNS!)";
            diagBuilder.AppendLine($"• DNS 'discord.com'                  : {discordDns}");
            string robloxDns = ResolveHost("roblox.com");
            if (robloxDns.Contains("195.175.")) robloxDns += " ⚠️ (BTK/ISS Engelleme IP'si - Zehirlenmiş DNS!)";
            diagBuilder.AppendLine($"• DNS 'roblox.com'                   : {robloxDns}");
            diagBuilder.AppendLine();

            // F. Kullanıcı Notu
            diagBuilder.AppendLine("[KULLANICI NOTU / AÇIKLAMA]");
            diagBuilder.AppendLine(string.IsNullOrWhiteSpace(userNote) ? "Kullanıcı not bırakmadı (Doğrudan log gönderildi)." : userNote.Trim());
            diagBuilder.AppendLine();

            // G. Son Sistem & WARP Logları
            diagBuilder.AppendLine("[SON LOGLAR (CIRCULAR BUFFER)]");
            diagBuilder.AppendLine(GetRecentLogsText(80));

            string fullDiagnosticText = diagBuilder.ToString();

            // 2. Yerel Diagnostics Dosyasına Kaydet
            try
            {
                string localDiagPath = Path.Combine(_diagnosticsDir, $"{reportId}.txt");
                await File.WriteAllTextAsync(localDiagPath, fullDiagnosticText, Encoding.UTF8);
            }
            catch { }

            // 3. Buluta Gönder:
            bool errorSent = false;
            bool eventSent = false;
            bool ticketSent = false;

            string summaryMsg = string.IsNullOrWhiteSpace(userNote)
                ? "Kullanıcı Lisans & Hesap kısmından sorun teşhisi için log ve sistem verilerini gönderdi."
                : $"[Kullanıcı Logu] {userNote.Trim()}";

            // A. AlpTogether Error/Diagnostic Tablosu (/alptogether/telemetry/errors/{ReportId}.json)
            try
            {
                var errorObj = new
                {
                    ReportId = reportId,
                    Timestamp = timestamp,
                    DeviceId = hwid,
                    MachineName = Environment.MachineName,
                    OsVersion = Environment.OSVersion.ToString(),
                    AppId = APP_ID,
                    AppName = APP_NAME,
                    AppVersion = APP_VERSION,
                    ErrorSource = "Kullanıcı Teşhis Logu (Lisans ve Hesap)",
                    ErrorMessage = summaryMsg,
                    StackTrace = fullDiagnosticText,
                    RecentLogs = GetRecentLogsText(50)
                };

                string fbErrorUrl = BuildFirebaseUrl($"alptogether/telemetry/errors/{reportId}.json");
                string jsonError = JsonSerializer.Serialize(errorObj);
                using var content = new StringContent(jsonError, Encoding.UTF8, "application/json");
                var resp = await _http.PutAsync(fbErrorUrl, content);
                errorSent = resp.IsSuccessStatusCode;
            }
            catch { }

            // B. AlpTogether Canlı Olay Akışı (/alptogether/telemetry/events/{EventId}.json)
            try
            {
                var eventObj = new
                {
                    EventId = eventId,
                    Timestamp = timestamp,
                    DeviceId = hwid,
                    MachineName = Environment.MachineName,
                    UserName = Environment.UserName,
                    EventType = "ERROR", // AlpTogether'da Hata filtresine düşer ve kırmızı rozet alır
                    AppId = APP_ID,
                    AppName = APP_NAME,
                    AppVersion = APP_VERSION,
                    Details = summaryMsg.Length > 200 ? summaryMsg[..200] + "..." : summaryMsg,
                    ExtraInfo = $"Rapor ID: {reportId}\nWireSock: {(wsServiceRunning ? "Aktif" : "Kapalı")} | Win: {Environment.OSVersion}\n{summaryMsg}"
                };

                string fbEvtUrl = BuildFirebaseUrl($"alptogether/telemetry/events/{eventId}.json");
                string jsonEvt = JsonSerializer.Serialize(eventObj);
                using var evtContent = new StringContent(jsonEvt, Encoding.UTF8, "application/json");
                var resp = await _http.PutAsync(fbEvtUrl, evtContent);
                eventSent = resp.IsSuccessStatusCode;
            }
            catch { }

            // C. AlpTogether Destek & Arıza Kayıtları (/alptogether/tickets/{TicketId}.json)
            try
            {
                var ticketObj = new
                {
                    TicketId = ticketId,
                    Timestamp = timestamp,
                    DeviceId = hwid,
                    MachineName = Environment.MachineName,
                    TargetAppId = APP_ID,
                    TargetAppName = APP_NAME,
                    TargetAppVersion = APP_VERSION,
                    IssueCategory = "Kullanıcı Teşhis & Arıza Bildirimi",
                    Severity = "Yüksek",
                    UserContact = $"{Environment.UserName} ({Environment.MachineName})",
                    Subject = $"AlpNet Pulse Teşhis & Log Bildirimi [{Environment.MachineName}]",
                    Description = summaryMsg,
                    AttachedSystemLogs = fullDiagnosticText,
                    Status = "Açık (Yeni)"
                };

                string fbTicketUrl = BuildFirebaseUrl($"alptogether/tickets/{ticketId}.json");
                string jsonTicket = JsonSerializer.Serialize(ticketObj);
                using var ticketContent = new StringContent(jsonTicket, Encoding.UTF8, "application/json");
                var resp = await _http.PutAsync(fbTicketUrl, ticketContent);
                ticketSent = resp.IsSuccessStatusCode;
            }
            catch { }

            bool anySuccess = errorSent || eventSent || ticketSent;
            if (anySuccess)
            {
                Log("TEŞHİS", $"Teşhis logları AlpTogether bulutuna başarıyla iletildi ({reportId}).", "SUCCESS");
                return (true, reportId, $"✅ Teşhis loglarınız geliştiriciye (AlpTogether) başarıyla iletildi!\nTakip Kodu: {reportId}");
            }
            else
            {
                Log("TEŞHİS", $"Teşhis logları yerel olarak kaydedildi, ancak internet bağlantısı veya bulut sunucusuna erişilemedi ({reportId}).", "WARN");
                return (false, reportId, $"⚠️ İnternet veya sunucu erişimi kurulamadı. Loglar yerel olarak kaydedildi: {reportId}");
            }
        }

        private static async Task<string> TestTcpPortAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var sw = Stopwatch.StartNew();
                var task = client.ConnectAsync(host, port);
                if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
                {
                    await task;
                    sw.Stop();
                    return $"ERİŞİLEBİLİR ({sw.ElapsedMilliseconds} ms)";
                }
                return $"ZAMAN AŞIMI (>{timeoutMs} ms) - ISP/Güvenlik Duvarı Engeli Olabilir";
            }
            catch (Exception ex)
            {
                return $"BAĞLANTI HATASI: {ex.Message}";
            }
        }

        private static string ResolveHost(string host)
        {
            try
            {
                var addresses = System.Net.Dns.GetHostAddresses(host);
                if (addresses.Length > 0)
                {
                    return string.Join(", ", addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()));
                }
                return "Adres çözülemedi (0 kayıt)";
            }
            catch (Exception ex)
            {
                return $"DNS Çözümleme Hatası: {ex.Message}";
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
        #endregion

        #region Global Unhandled Exception Hook
        /// <summary>
        /// Tüm uygulama çökmelerini otomatik olarak yakalar ve AlpTogether telemetrisine bildirir.
        /// </summary>
        public void HookGlobalExceptions()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Error("ÇÖKME", $"AppDomain UnhandledException: {ex.Message}\n{ex.StackTrace}");
                    _ = ReportErrorImmediatelyAsync("AppDomain.UnhandledException", ex.Message, ex.ToString());
                }
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Error("ÇÖKME", $"TaskScheduler UnobservedTaskException: {e.Exception.Message}\n{e.Exception.StackTrace}");
                _ = ReportErrorImmediatelyAsync("TaskScheduler.UnobservedTaskException", e.Exception.Message, e.Exception.ToString());
                e.SetObserved();
            };

            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.DispatcherUnhandledException += (s, e) =>
                {
                    Error("ÇÖKME", $"DispatcherUnhandledException: {e.Exception.Message}\n{e.Exception.StackTrace}");
                    _ = ReportErrorImmediatelyAsync("DispatcherUnhandledException", e.Exception.Message, e.Exception.ToString());
                };
            }
        }
        #endregion
    }
}
