using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AlpNetPulse
{
    public class WarpManager
    {
        // ═══════════════════════════════════════════════════════════
        //  SAF HEDEF LİSTESİ: YALNIZCA DISCORD VE ROBLOX (TÜM ALT ALANLAR DAHİL)
        // ═══════════════════════════════════════════════════════════
        private static readonly string[] TargetDomains = new[]
        {
            "discord.com", "api.discord.com", "status.discord.com", "discordapp.com", "discordapp.net",
            "discord.gg", "gateway.discord.gg", "cdn.discordapp.com",
            "media.discordapp.net", "images-ext-1.discordapp.net", "images-ext-2.discordapp.net",
            "router.discordapp.net", "latency.discord.media",
            "updates.discord.com", "dl.discordapp.net", "discord.gift", "discord.media",
            "discordcdn.com", "discord-attachments-uploads-prd.storage.googleapis.com",
            "roblox.com", "rbxcdn.com", "robloxlabs.com", "setup.rbxcdn.com", "clientsettings.roblox.com"
        };

        private readonly string _resDir;
        private readonly string _wgcfPath;
        private readonly HttpClient _http;

        public static readonly int[] FallbackPorts = new[] { 500, 2408, 4500, 1701 };
        public int CurrentPort { get; private set; } = 500;

        public DateTime? WarpStartedAt { get; private set; }
        public DateTime? WarpStoppedAt { get; private set; }
        public double WarpTotalHoursToday { get; private set; }

        private System.Threading.Timer? _watchdogTimer;
        private int _consecutiveFailures = 0;
        private Action<string>? _activeLogCallback;

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive || IsWireSockServiceRunning();
            private set => _isActive = value;
        }
        public bool IsBrowserVpnEnabled { get; set; } = false;

        public WarpManager(string? resDir = null)
        {
            _resDir = string.IsNullOrEmpty(resDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res")
                : resDir;

            if (!Directory.Exists(_resDir))
            {
                try { Directory.CreateDirectory(_resDir); } catch { }
            }

            _wgcfPath = Path.Combine(_resDir, "wgcf.exe");

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _http.DefaultRequestHeaders.Add("User-Agent", "AlpNetPulse/2.2.8");

            LoadUsageToday();
        }

        public const string CollidingSharedKey = "ID6Va+b6e5vusYdSh8PkhB+Z8gOVPZFtq2/fni+1K1Q=";

        private const string DefaultFallbackProfile = 
@"[Interface]
PrivateKey = ID6Va+b6e5vusYdSh8PkhB+Z8gOVPZFtq2/fni+1K1Q=
Address = 172.16.0.2/32, 2606:4700:110:8369:e40f:6f09:1250:1c64/128
MTU = 1360

[Peer]
PublicKey = bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = 162.159.192.1:500
PersistentKeepalive = 15
";

        // ═══════════════════════════════════════════════════════════
        //  WARP BAŞLATMA (ULTRA HIZLI VE STABİL BAŞLATMA)
        // ═══════════════════════════════════════════════════════════
        public async Task<bool> StartWarpAsync(Action<string> logCallback)
        {
            var origLogCallback = logCallback;
            logCallback = (msg) =>
            {
                AlpNetTelemetryService.Instance.Log("WARP", msg);
                origLogCallback?.Invoke(msg);
            };

            try
            {
                if (IsActive)
                {
                    logCallback("WARP zaten aktif.");
                    return true;
                }

                // Eski artık servisleri temizle
                PurgeDpiServices();

                // Adım 1: WireSock kurulu mu?
                var wireSockExe = FindWireSockPath();
                if (string.IsNullOrEmpty(wireSockExe))
                {
                    logCallback("WireSock VPN bileşeni yükleniyor...");
                    bool installed = await SilentInstallWireSockAsync(logCallback);
                    if (!installed)
                    {
                        logCallback("WireSock VPN kurulumu başarısız!");
                        return false;
                    }
                    wireSockExe = FindWireSockPath();
                    if (string.IsNullOrEmpty(wireSockExe))
                    {
                        logCallback("WireSock bulunamadı!");
                        return false;
                    }
                }

                // Adım 2: Profil var mı kontrol et, yoksa veya çakışan ortak anahtarlıysa yeniden üret
                var profilePath = Path.Combine(_resDir, "wgcf-profile.conf");
                var accountFile = Path.Combine(_resDir, "wgcf-account.toml");
                bool needNewProfile = false;

                if (!File.Exists(profilePath) || new FileInfo(profilePath).Length < 50)
                {
                    needNewProfile = true;
                }
                else
                {
                    string existingProfile = await File.ReadAllTextAsync(profilePath);
                    if (!existingProfile.Contains("PrivateKey") || !existingProfile.Contains("PublicKey") || existingProfile.Contains(CollidingSharedKey))
                    {
                        needNewProfile = true;
                    }
                }

                if (needNewProfile)
                {
                    logCallback("Bu cihaza özel benzersiz Cloudflare WARP kimliği hazırlanıyor...");

                    if (File.Exists(accountFile))
                    {
                        try { File.Delete(accountFile); } catch { }
                    }
                    if (File.Exists(profilePath))
                    {
                        try { File.Delete(profilePath); } catch { }
                    }

                    var wgcfPath = await DownloadWgcfAsync();
                    bool generatedSuccessfully = false;

                    if (!string.IsNullOrEmpty(wgcfPath))
                    {
                        logCallback("Cloudflare WARP benzersiz cihaz kaydı yapılıyor...");
                        await ExecuteCommandAsync(wgcfPath, "register --accept-tos");
                        logCallback("Tünel profili oluşturuluyor...");
                        await ExecuteCommandAsync(wgcfPath, "generate");

                        if (File.Exists(profilePath) && new FileInfo(profilePath).Length > 100)
                        {
                            string genText = await File.ReadAllTextAsync(profilePath);
                            if (genText.Contains("PrivateKey") && !genText.Contains(CollidingSharedKey))
                            {
                                generatedSuccessfully = true;
                            }
                        }
                    }

                    // wgcf başarısız olsa bile yerleşik yedek Cloudflare WARP profilini devreye al
                    if (!generatedSuccessfully && (!File.Exists(profilePath) || new FileInfo(profilePath).Length < 50))
                    {
                        await File.WriteAllTextAsync(profilePath, DefaultFallbackProfile);
                    }
                }

                // Split tunnel yapılandırmasını doğrula (Port 500, PersistentKeepalive=25, IPv6 ve temiz AllowedApps)
                await ModifyConfigurationAsync(profilePath);

                // Adım 4: WireSock servisini güvenli ve ultra hızlı başlat (~150ms)
                logCallback("WARP tüneli başlatılıyor...");
                string installArgs = $"install -start-type 2 -config \"{profilePath}\" -log-level none";

                bool isInstalled = IsWireSockServiceInstalled();
                if (isInstalled && IsWireSockServiceRunning())
                {
                    logCallback("WireSock servisi devrede, tünel hazır.");
                }
                else if (isInstalled)
                {
                    logCallback("WireSock servisi ultra hızlı başlatılıyor (~150ms)...");
                    int startRes = await ExecuteCommandAsync("net", "start wiresock-client-service");
                    if (startRes != 0)
                    {
                        startRes = await ExecuteCommandAsync("sc", "start wiresock-client-service");
                    }
                    if (startRes != 0)
                    {
                        // Servis parametreleri değişmişse temiz kur
                        await ExecuteCommandAsync(wireSockExe, "uninstall");
                        await Task.Delay(100);
                        await ExecuteCommandAsync(wireSockExe, installArgs);
                        await Task.Delay(100);
                        await ExecuteCommandAsync("net", "start wiresock-client-service");
                    }
                }
                else
                {
                    logCallback("WireSock servisi yapılandırılıyor...");
                    await ExecuteCommandAsync(wireSockExe, installArgs);
                    await Task.Delay(150);
                    int startResult = await ExecuteCommandAsync("net", "start wiresock-client-service");
                    if (startResult != 0)
                    {
                        await ExecuteCommandAsync("sc", "start wiresock-client-service");
                    }
                }

                // DNS ayarla, HTTP/3 QUIC etkinleştir ve hedef domainleri ön ısıt (DNS warmup)
                _ = Task.Run(async () =>
                {
                    EnsureBrowserHttp3QuicEnabled();
                    await SetCloudflareDnsAsync();
                    await WarmupTargetDomainsAsync();
                });

                logCallback("🌐 WARP Split Tüneli Aktif!");
                IsActive = true;
                WarpStartedAt = DateTime.UtcNow;
                LoadUsageToday();
                StartWatchdog(logCallback);
                return true;
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Error("WARP", $"WARP başlatma hatası: {ex.Message}");
                _ = AlpNetTelemetryService.Instance.ReportErrorImmediatelyAsync("WARP.StartWarpAsync", ex.Message, ex.ToString());
                logCallback("Hata: " + ex.Message);
                IsActive = false;
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  DURDURMA
        // ═══════════════════════════════════════════════════════════
        public void StopWarp()
        {
            try
            {
                AlpNetTelemetryService.Instance.Log("WARP", "WARP Split Tüneli durduruluyor...");
                StopWatchdog();
                WarpStoppedAt = DateTime.UtcNow;
                if (WarpStartedAt.HasValue)
                {
                    WarpTotalHoursToday += (DateTime.UtcNow - WarpStartedAt.Value).TotalHours;
                    SaveUsageToday();
                }

                // Hızlı durdurma (Uninstall yapmadan servisi durdurur, böylece sonraki açılış 150ms olur)
                Task.Run(async () =>
                {
                    await ExecuteCommandAsync("net", "stop wiresock-client-service");
                }).Wait(2000);

                // DNS sıfırla (IPv4 + IPv6)
                Task.Run(() =>
                {
                    try
                    {
                        string netshCmd = "/c " +
                            "netsh interface ip set dns name=\"Ethernet\" source=dhcp 2>nul & " +
                            "netsh interface ip set dns name=\"Wi-Fi\" source=dhcp 2>nul & " +
                            "netsh interface ipv6 set dns name=\"Ethernet\" source=dhcp 2>nul & " +
                            "netsh interface ipv6 set dns name=\"Wi-Fi\" source=dhcp 2>nul & " +
                            "ipconfig /flushdns";
                        var flushPsi = new ProcessStartInfo("cmd.exe", netshCmd)
                        {
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var pNetsh = Process.Start(flushPsi);
                        pNetsh?.WaitForExit(2000);

                        string psScript = 
                            "Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { " +
                            "  Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ResetServerAddresses -ErrorAction SilentlyContinue " +
                            "}; " +
                            "if (Get-Command Set-DnsClientDohServerAddress -ErrorAction SilentlyContinue) { " +
                            "  Set-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -AutoUpgrade $false -ErrorAction SilentlyContinue; " +
                            "  Set-DnsClientDohServerAddress -ServerAddress '1.0.0.1' -AutoUpgrade $false -ErrorAction SilentlyContinue; " +
                            "}; " +
                            "Clear-DnsClientCache; ipconfig /flushdns";

                        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"")
                        {
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(4000);
                    }
                    catch { }
                });

                IsActive = false;
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  SESSİZ WIRESOCK KURULUMU
        // ═══════════════════════════════════════════════════════════
        private async Task<bool> SilentInstallWireSockAsync(Action<string> logCallback)
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "AlpNetInstall");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                var assembly = Assembly.GetExecutingAssembly();

                // 1) Windows Packet Filter driver MSI'ı çıkar ve kur
                logCallback("Ağ driver'ı kuruluyor...");
                string wpfMsiPath = Path.Combine(tempDir, "wpf_driver.msi");
                if (ExtractEmbeddedResource(assembly, "AlpNetPulse.Resources.wpf_driver.msi", wpfMsiPath))
                {
                    await RunMsiSilentAsync(wpfMsiPath);
                }

                await Task.Delay(500);

                // 2) WireSock VPN Client MSI'ı çıkar ve kur
                logCallback("WireSock VPN kuruluyor...");
                string wireSockMsiPath = Path.Combine(tempDir, "wiresock.msi");
                if (ExtractEmbeddedResource(assembly, "AlpNetPulse.Resources.wiresock.msi", wireSockMsiPath))
                {
                    await RunMsiSilentAsync(wireSockMsiPath);
                }

                // Kurulumun tamamlanmasını polling ile bekle (20 x 500ms = 10 saniye)
                string? path = null;
                for (int i = 0; i < 20; i++)
                {
                    path = FindWireSockPath();
                    if (!string.IsNullOrEmpty(path))
                        return true;
                    await Task.Delay(500);
                }

                return !string.IsNullOrEmpty(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SilentInstallWireSock failed: {ex.Message}");
                return false;
            }
        }

        private bool ExtractEmbeddedResource(Assembly assembly, string resourceName, string outputPath)
        {
            try
            {
                Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    string simpleName = Path.GetFileName(outputPath);
                    var matchedName = assembly.GetManifestResourceNames()
                        .FirstOrDefault(n => n.EndsWith(simpleName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(matchedName))
                    {
                        stream = assembly.GetManifestResourceStream(matchedName);
                    }
                }

                if (stream == null) return false;

                using (stream)
                using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fs);
                }
                return true;
            }
            catch { return false; }
        }

        private async Task<int> RunMsiSilentAsync(string msiPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 1) Doğrudan çalışan yükseltilmiş süreçten msiexec çalıştır
                    var psi = new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\" /qn /norestart ALLUSERS=1")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(60000);
                    int code = proc?.ExitCode ?? -1;
                    if (code == 0 || code == 3010) return code;

                    // 2) Fallback: runas ile yükseltilmiş başlatma
                    var psiFallback = new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\" /qn /norestart ALLUSERS=1")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var procFb = Process.Start(psiFallback);
                    procFb?.WaitForExit(60000);
                    return procFb?.ExitCode ?? -1;
                }
                catch { return -1; }
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  CONFIG'E ALLOWEDAPPS EKLE & PORT 500 + KEEPALIVE 25 UYGULA
        // ═══════════════════════════════════════════════════════════
        public async Task<bool> ModifyConfigurationAsync(string profilePath)
        {
            try
            {
                var allowedApps = new List<string>
                {
                    // Discord Resmi İstemcileri ve Süreçleri (hem .exe hem uzantısız)
                    "discord", "Discord.exe",
                    "discordptb", "DiscordPTB.exe",
                    "discordcanary", "DiscordCanary.exe",
                    "discorddevelopment", "DiscordDevelopment.exe",
                    "update", "Update.exe",
                    "squirrel", "Squirrel.exe",
                    // Popüler Discord Alternatif/Gelişmiş İstemcileri (Vesktop, WebCord vb.)
                    "vesktop", "Vesktop.exe",
                    "webcord", "WebCord.exe", "webcord.exe",
                    "armcord", "ArmCord.exe", "armcord.exe",
                    "legcord", "Legcord.exe", "legcord.exe",
                    "ripcord", "Ripcord.exe", "ripcord.exe",
                    "electron", "electron.exe",
                    // Roblox
                    "roblox", "RobloxPlayerBeta.exe", "RobloxPlayerInstaller.exe",
                    "RobloxStudioBeta.exe", "RobloxStudioInstaller.exe", "RobloxCrashHandler.exe",
                    // AlpNet Pulse
                    "AlpNet.Pulse.exe", "AlpNetPulse.exe"
                };

                if (IsBrowserVpnEnabled)
                {
                    allowedApps.AddRange(new[]
                    {
                        "chrome", "chrome.exe",
                        "msedge", "msedge.exe",
                        "firefox", "firefox.exe",
                        "brave", "brave.exe",
                        "opera", "opera.exe",
                        "operagx", "operagx.exe", "opera_gx.exe",
                        "vivaldi", "vivaldi.exe",
                        "zen", "zen.exe",
                        "waterfox", "waterfox.exe",
                        "chromium", "chromium.exe",
                        "tor", "tor.exe",
                        "librewolf", "librewolf.exe",
                        "arc", "arc.exe",
                        "browser", "browser.exe",
                        "yandex", "yandex.exe"
                    });
                }

                var allowedAppsLine = $"AllowedApps = {string.Join(", ", allowedApps.Distinct(StringComparer.OrdinalIgnoreCase))}";

                var lines = (await File.ReadAllLinesAsync(profilePath)).ToList();

                // Eğer Address içinde IPv6 eksikse, wgcf generate ile profili yeniden tazeleyip oku
                bool hasIpv6Address = lines.Any(l => l.Trim().StartsWith("Address", StringComparison.OrdinalIgnoreCase) && l.Contains(":"));
                if (!hasIpv6Address && File.Exists(Path.Combine(_resDir, "wgcf-account.toml")) && File.Exists(_wgcfPath))
                {
                    try
                    {
                        await ExecuteCommandAsync(_wgcfPath, "generate");
                        lines = (await File.ReadAllLinesAsync(profilePath)).ToList();
                    }
                    catch { }
                }

                // Endpoint IP: DNS zehirlenmesi ve çözümlenememe sorunlarını önlemek için doğrudan Cloudflare Anycast IP'si kullan
                string endpointIp = "162.159.192.1";
                try
                {
                    var resolved = System.Net.Dns.GetHostAddresses("engage.cloudflareclient.com");
                    if (resolved != null && resolved.Length > 0)
                    {
                        var validIp = resolved.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip));
                        if (validIp != null)
                        {
                            endpointIp = validIp.ToString();
                        }
                    }
                }
                catch { }
                string endpointLine = $"Endpoint = {endpointIp}:{CurrentPort}";

                var newLines = new List<string>();
                bool inPeerSection = false;
                bool keepaliveAdded = false;
                bool allowedAppsAdded = false;
                bool mtuAdded = false;

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();

                    // Mükerrer / eski AllowedApps, DisallowedApps, AllowedDomains, PersistentKeepalive ve DNS satırlarını atla
                    // ÖNEMLİ: DNS satırı profilden tamamen temizlenir. Böylece WireSock yalnızca AllowedApps içindeki Discord/Roblox'u tüneller;
                    // Rust, Steam, Easy Anti-Cheat ve diğer oyunların sistem DNS sorgularına ASLA dokunmaz!
                    if (trimmed.StartsWith("AllowedApps", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("DisallowedApps", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("AllowedDomains", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("DisallowedDomains", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("PersistentKeepalive", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (trimmed.StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase))
                    {
                        // [Interface] bitmeden MTU eklenmediyse araya enjekte et
                        if (!mtuAdded)
                        {
                            newLines.Add("MTU = 1360");
                            mtuAdded = true;
                        }

                        inPeerSection = true;
                        newLines.Add(line);
                        continue;
                    }

                    if (trimmed.StartsWith("MTU", StringComparison.OrdinalIgnoreCase))
                    {
                        newLines.Add("MTU = 1360");
                        mtuAdded = true;
                        continue;
                    }

                    if (inPeerSection)
                    {
                        if (trimmed.StartsWith("AllowedIPs", StringComparison.OrdinalIgnoreCase))
                        {
                            // Hem IPv4 hem IPv6'yı kapsayarak ISP IPv6 engeline takılmayı engelle
                            newLines.Add("AllowedIPs = 0.0.0.0/0, ::/0");
                            continue;
                        }

                        if (trimmed.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
                        {
                            // Türkiye'de 2408 UDP ISP engeline takılmamak için Port 500 kullanılır
                            newLines.Add(endpointLine);
                            newLines.Add("PersistentKeepalive = 15");
                            newLines.Add(allowedAppsLine);
                            keepaliveAdded = true;
                            allowedAppsAdded = true;
                            continue;
                        }
                    }

                    newLines.Add(line);
                }

                // Eğer Endpoint Peer altında bulunamadıysa bile Peer sonuna ekle
                if (inPeerSection)
                {
                    if (!keepaliveAdded) newLines.Add("PersistentKeepalive = 15");
                    if (!allowedAppsAdded) newLines.Add(allowedAppsLine);
                }

                await File.WriteAllLinesAsync(profilePath, newLines);

                // Eğer AppData altındaki AlpNet res dizini farklıysa oraya da senkronize et
                try
                {
                    string appDataRes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet", "res");
                    string appDataProfile = Path.Combine(appDataRes, "wgcf-profile.conf");
                    if (Directory.Exists(appDataRes) && !string.Equals(Path.GetFullPath(profilePath), Path.GetFullPath(appDataProfile), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(profilePath, appDataProfile, true);
                    }

                    if (!string.IsNullOrEmpty(_resDir) && Directory.Exists(_resDir))
                    {
                        string resProfile = Path.Combine(_resDir, "wgcf-profile.conf");
                        if (!string.Equals(Path.GetFullPath(profilePath), Path.GetFullPath(resProfile), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(profilePath, resProfile, true);
                        }
                    }
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Config modification error: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  TARAYICI TÜNELİ (BROWSER VPN) DİNAMİK AÇ/KAPAT
        // ═══════════════════════════════════════════════════════════
        public async Task<bool> SetBrowserVpnAsync(bool enable, Action<string>? logCallback = null)
        {
            IsBrowserVpnEnabled = enable;
            if (enable)
            {
                EnsureBrowserHttp3QuicEnabled();
            }

            if (!IsActive)
                return true;

            try
            {
                var wireSockExe = FindWireSockPath();
                var profilePath = Path.Combine(_resDir, "wgcf-profile.conf");

                if (string.IsNullOrEmpty(wireSockExe) || !File.Exists(profilePath))
                    return false;

                logCallback?.Invoke(enable ? "🌐 Tarayıcı Tüneli Aktif Ediliyor..." : "🌐 Tarayıcı Tüneli Kapatılıyor...");

                await ModifyConfigurationAsync(profilePath);

                // WireSock servisini güncel profille güvenli şekilde yeniden başlat
                await ExecuteCommandAsync("net", "stop wiresock-client-service");
                await Task.Delay(150);
                await ExecuteCommandAsync(wireSockExe, "uninstall");
                await Task.Delay(150);

                string installArgs = $"install -start-type 2 -config \"{profilePath}\" -log-level none";
                await ExecuteCommandAsync(wireSockExe, installArgs);
                await Task.Delay(150);

                int startResult = await ExecuteCommandAsync("net", "start wiresock-client-service");
                if (startResult != 0)
                    await ExecuteCommandAsync("sc", "start wiresock-client-service");

                _ = Task.Run(SetCloudflareDnsAsync);

                logCallback?.Invoke(enable ? "🌐 Tarayıcı Tüneli Açık!" : "🌐 Tarayıcı Tüneli Kapalı (Sadece Discord & Roblox)");
                return true;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke("Hata: " + ex.Message);
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  PRE-DNS WARMUP ENGINE (Discord & Roblox Hızlı Ön Isıtma)
        // ═══════════════════════════════════════════════════════════
        private static async Task WarmupTargetDomainsAsync()
        {
            try
            {
                var tasks = new List<Task>();
                foreach (var d in TargetDomains)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            using var cts = new System.Threading.CancellationTokenSource(3000);
                            await System.Net.Dns.GetHostAddressesAsync(d, cts.Token);
                        }
                        catch { }
                    }));
                }
                await Task.WhenAll(tasks);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  wgcf.exe İndir (Varsa Doğrudan Kullan, Yoksa Gömülü Kaynaktan Çıkar / İndir)
        // ═══════════════════════════════════════════════════════════
        public async Task<string?> DownloadWgcfAsync()
        {
            if (File.Exists(_wgcfPath) && new FileInfo(_wgcfPath).Length > 1000000)
                return _wgcfPath;

            // 1) Gömülü kaynaktan (Embedded Resource) anında çıkar (~50ms)
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                if (ExtractEmbeddedResource(assembly, "AlpNetPulse.Resources.wgcf.exe", _wgcfPath))
                {
                    if (File.Exists(_wgcfPath) && new FileInfo(_wgcfPath).Length > 1000000)
                        return _wgcfPath;
                }
            }
            catch { }

            // 2) Yerel fallback dizinlerini kontrol et
            try
            {
                string[] fallbacks = {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res", "wgcf.exe"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wgcf.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet", "res", "wgcf.exe"),
                    @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpNet\res\wgcf.exe"
                };
                foreach (var fb in fallbacks)
                {
                    if (File.Exists(fb))
                    {
                        File.Copy(fb, _wgcfPath, true);
                        return _wgcfPath;
                    }
                }
            }
            catch { }

            try
            {
                var releasesUrl = "https://api.github.com/repos/ViRb3/wgcf/releases/latest";
                var releasesResponse = await _http.GetStringAsync(releasesUrl);

                var assetMatch = Regex.Match(releasesResponse,
                    @"""browser_download_url"":\s*""([^""]*wgcf_[^""]*_windows_amd64[^""]*)""");

                if (assetMatch.Success)
                {
                    var downloadUrl = assetMatch.Groups[1].Value;
                    var response = await _http.GetAsync(downloadUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        using (var fs = File.Create(_wgcfPath))
                        {
                            await response.Content.CopyToAsync(fs);
                        }
                        if (File.Exists(_wgcfPath) && new FileInfo(_wgcfPath).Length > 100000)
                            return _wgcfPath;
                    }
                }
            }
            catch { }

            // Fallback: Doğrudan bilinen çalışan ikili URL'leri dene (GitHub API kotasına takılmadan)
            string[] directUrls = new[]
            {
                "https://github.com/ViRb3/wgcf/releases/download/v2.2.23/wgcf_2.2.23_windows_amd64.exe",
                "https://github.com/ViRb3/wgcf/releases/download/v2.2.22/wgcf_2.2.22_windows_amd64.exe"
            };

            foreach (var url in directUrls)
            {
                try
                {
                    var response = await _http.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        using (var fs = File.Create(_wgcfPath))
                        {
                            await response.Content.CopyToAsync(fs);
                        }
                        if (File.Exists(_wgcfPath) && new FileInfo(_wgcfPath).Length > 100000)
                            return _wgcfPath;
                    }
                }
                catch { }
            }

            return File.Exists(_wgcfPath) ? _wgcfPath : null;
        }

        // ═══════════════════════════════════════════════════════════
        //  WireSock Yolu Bul
        // ═══════════════════════════════════════════════════════════
        public static string? FindWireSockPath()
        {
            var candidateBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddProgDir(string? dir)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    candidateBases.Add(dir);
            }

            AddProgDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddProgDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            AddProgDir(Environment.GetEnvironmentVariable("ProgramW6432"));
            AddProgDir(Environment.GetEnvironmentVariable("ProgramFiles"));
            AddProgDir(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));

            foreach (var baseDir in candidateBases)
            {
                try
                {
                    var p1 = Path.Combine(baseDir, "WireSock Secure Connect", "bin", "wiresock-client.exe");
                    if (File.Exists(p1)) return p1;
                    var p2 = Path.Combine(baseDir, "WireSock VPN Client", "bin", "wiresock-client.exe");
                    if (File.Exists(p2)) return p2;
                }
                catch { }
            }

            var targetDrives = new[] { "C:", "D:", "E:" };
            foreach (var driveLetter in targetDrives)
            {
                try
                {
                    var drive = new DriveInfo(driveLetter);
                    if (!drive.IsReady) continue;
                    var paths = new[]
                    {
                        Path.Combine(drive.Name, "Program Files", "WireSock Secure Connect", "bin", "wiresock-client.exe"),
                        Path.Combine(drive.Name, "Program Files (x86)", "WireSock Secure Connect", "bin", "wiresock-client.exe"),
                        Path.Combine(drive.Name, "Program Files", "WireSock VPN Client", "bin", "wiresock-client.exe"),
                        Path.Combine(drive.Name, "Program Files (x86)", "WireSock VPN Client", "bin", "wiresock-client.exe")
                    };
                    foreach (var path in paths)
                    {
                        if (File.Exists(path)) return path;
                    }
                }
                catch { continue; }
            }
            return null;
        }

        // ═══════════════════════════════════════════════════════════
        //  DNS Ayarla (Güvenli IPv4 + Windows 11 DoH Şifreleme)
        // ═══════════════════════════════════════════════════════════
        private static async Task SetCloudflareDnsAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    // 1) Hızlı netsh ile ana bağdaştırıcılara anında uygula (~20ms)
                    // Türk ISS'lerinde yerel IPv6 yönlendirmesi bulunmadığından, IPv6 DNS atanması Windows DNS istemcisinde
                    // 2 saniyelik timeout/blackhole gecikmelerine neden olur. Bu yüzden IPv4 1.1.1.1/1.0.0.1 atanır, IPv6 DHCP'ye bırakılır.
                    string netshCmd = "/c " +
                        "netsh interface ip set dns name=\"Wi-Fi\" static 1.1.1.1 primary 2>nul & " +
                        "netsh interface ip add dns name=\"Wi-Fi\" 1.0.0.1 index=2 2>nul & " +
                        "netsh interface ip set dns name=\"Ethernet\" static 1.1.1.1 primary 2>nul & " +
                        "netsh interface ip add dns name=\"Ethernet\" 1.0.0.1 index=2 2>nul & " +
                        "netsh interface ipv6 set dns name=\"Wi-Fi\" source=dhcp 2>nul & " +
                        "netsh interface ipv6 set dns name=\"Ethernet\" source=dhcp 2>nul";

                    var netshPsi = new ProcessStartInfo("cmd.exe", netshCmd)
                    {
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var p1 = Process.Start(netshPsi);
                    p1?.WaitForExit(2000);

                    // 2) PowerShell ile TÜM aktif bağdaştırıcıları ayarla: IPv4 DNS Cloudflare, IPv6 DNS sıfırla (timeout engelle)
                    // DoH AutoUpgrade devre dışı bırakılır (AutoUpgrade=$false); böylece oyunlar (Rust, Steam) ve Easy Anti-Cheat standart UDP port 53 üzerinden engelsiz ve 0 gecikmeyle çalışır.
                    string psCmd = 
                        "$adapters = Get-NetAdapter | Where-Object Status -eq 'Up'; " +
                        "$adapters | ForEach-Object { " +
                        "  Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ServerAddresses ('1.1.1.1', '1.0.0.1') -ErrorAction SilentlyContinue; " +
                        "  Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -Family IPv6 -ResetServerAddresses -ErrorAction SilentlyContinue " +
                        "}; " +
                        "if (Get-Command Set-DnsClientDohServerAddress -ErrorAction SilentlyContinue) { " +
                        "  Set-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -AutoUpgrade $false -ErrorAction SilentlyContinue; " +
                        "  Set-DnsClientDohServerAddress -ServerAddress '1.0.0.1' -AutoUpgrade $false -ErrorAction SilentlyContinue; " +
                        "}; " +
                        "Clear-DnsClientCache; ipconfig /flushdns";

                    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCmd}\"")
                    {
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var p2 = Process.Start(psi);
                    p2?.WaitForExit(6000);
                }
                catch { }
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  TARAYICILARDA HTTP/3 (QUIC - UDP 443) ZORLAMA / ETKİNLEŞTİRME
        // ═══════════════════════════════════════════════════════════
        public static void EnsureBrowserHttp3QuicEnabled()
        {
            try
            {
                string[] policyPaths = new[]
                {
                    @"Software\Policies\Google\Chrome",
                    @"Software\Policies\Microsoft\Edge",
                    @"Software\Policies\BraveSoftware\Brave"
                };

                foreach (var path in policyPaths)
                {
                    try
                    {
                        using var cuKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path);
                        cuKey?.SetValue("QuicAllowed", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    }
                    catch { }

                    try
                    {
                        using var lmKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                        lmKey?.SetValue("QuicAllowed", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  DISCORD ÖNBELLEK VE BAĞLANTI ONARIMI (1 TIKLA OTOMATİK ÇÖZÜM)
        // ═══════════════════════════════════════════════════════════
        public static async Task<bool> RepairDiscordCacheAsync(Action<string>? logCallback = null)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    logCallback?.Invoke("Discord ve istemci süreçleri güvenle kapatılıyor...");
                    string[] discordProcs = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment", "Vesktop", "webcord", "ArmCord", "Legcord", "Ripcord" };
                    
                    // 1) .NET Process.Kill(entireProcessTree: true)
                    foreach (var name in discordProcs)
                    {
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            try { p.Kill(true); p.WaitForExit(1500); } catch { }
                        }
                    }

                    // Only kill Update.exe processes that belong to Discord or Discord clients (prevent killing Spotify, VS Code, etc.)
                    foreach (var p in Process.GetProcessesByName("Update"))
                    {
                        try
                        {
                            string? exePath = null;
                            try { exePath = p.MainModule?.FileName; } catch { }
                            if (!string.IsNullOrEmpty(exePath) && 
                                (exePath.IndexOf("Discord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 exePath.IndexOf("Vesktop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 exePath.IndexOf("webcord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 exePath.IndexOf("armcord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 exePath.IndexOf("legcord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 exePath.IndexOf("ripcord", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                p.Kill(true);
                                p.WaitForExit(1500);
                            }
                        }
                        catch { }
                    }

                    // 2) Kalan kilitli alt süreçleri zorla sonlandır (Update.exe kaldırıldı)
                    try
                    {
                        var killPsi = new ProcessStartInfo("cmd.exe", "/c taskkill /F /T /IM Discord.exe /IM DiscordPTB.exe /IM DiscordCanary.exe /IM DiscordDevelopment.exe /IM Vesktop.exe /IM webcord.exe /IM ArmCord.exe 2>nul")
                        {
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var pKill = Process.Start(killPsi);
                        pKill?.WaitForExit(2000);
                    }
                    catch { }

                    System.Threading.Thread.Sleep(300);

                    logCallback?.Invoke("Bozuk Discord ağ soketleri, oturum kayıtları ve önbellekleri temizleniyor...");
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string[] discordDirs = { "discord", "discordptb", "discordcanary", "discorddevelopment", "vesktop", "webcord", "ArmCord", "Legcord", "Ripcord" };

                    foreach (var d in discordDirs)
                    {
                        // A) Roaming AppData (Chromium Network, Alt-Svc bozuk soketler ve önbellekler)
                        var fullDir = Path.Combine(appData, d);
                        if (Directory.Exists(fullDir))
                        {
                            // 1) Chromium Network State ve Alt-Svc / HTTP/2 bozuk durum dosyalarını sıfırla
                            var networkDir = Path.Combine(fullDir, "Network");
                            if (Directory.Exists(networkDir))
                            {
                                string[] networkFiles = { "Network Persistent State", "TransportSecurity", "Trust Tokens", "Trust Tokens-journal" };
                                foreach (var nf in networkFiles)
                                {
                                    var fPath = Path.Combine(networkDir, nf);
                                    if (File.Exists(fPath)) { try { File.Delete(fPath); } catch { } }
                                }
                            }

                            // 2) Önbellek ve mesaj senkronizasyon dizinlerini temizle (Local Storage korunur, oturum kapanmaz)
                            string[] cacheFolders = { 
                                "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", 
                                "blob_storage", "Session Storage", "IndexedDB", "Service Worker", "Shared Dictionary", 
                                "shared_proto_db", "WebStorage", Path.Combine("Network", "Cache"), Path.Combine("Network", "Code Cache") 
                            };
                            foreach (var cf in cacheFolders)
                            {
                                var target = Path.Combine(fullDir, cf);
                                if (Directory.Exists(target))
                                {
                                    try { Directory.Delete(target, true); } catch { }
                                }
                            }
                        }

                        // B) Local AppData önbellekleri
                        var locDir = Path.Combine(localAppData, d);
                        if (Directory.Exists(locDir))
                        {
                            string[] locCache = { "Cache", "Code Cache", "GPUCache", "CrashDumps" };
                            foreach (var cf in locCache)
                            {
                                var target = Path.Combine(locDir, cf);
                                if (Directory.Exists(target))
                                {
                                    try { Directory.Delete(target, true); } catch { }
                                }
                            }
                        }
                    }

                    // 3) Çakışan ortak anahtarlı profil kontrolü ve cihaza özel benzersiz kimlik onarımı
                    try
                    {
                        string[] candidateResDirs = {
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet", "res")
                        };

                        foreach (var resD in candidateResDirs)
                        {
                            if (!Directory.Exists(resD)) continue;
                            var profFile = Path.Combine(resD, "wgcf-profile.conf");
                            if (File.Exists(profFile))
                            {
                                string pText = File.ReadAllText(profFile);
                                if (pText.Contains(CollidingSharedKey))
                                {
                                    logCallback?.Invoke("Çakışan ağ kimliği tespit edildi, bu cihaza özel kimlik üretiliyor...");
                                    var dummyWarp = new WarpManager(resD);
                                    var wgcf = await dummyWarp.DownloadWgcfAsync();
                                    if (!string.IsNullOrEmpty(wgcf))
                                    {
                                        var accFile = Path.Combine(resD, "wgcf-account.toml");
                                        try { File.Delete(profFile); } catch { }
                                        try { File.Delete(accFile); } catch { }

                                        await dummyWarp.ExecuteCommandAsync(wgcf, "register --accept-tos");
                                        await dummyWarp.ExecuteCommandAsync(wgcf, "generate");

                                        if (File.Exists(profFile))
                                        {
                                            await dummyWarp.ModifyConfigurationAsync(profFile);
                                            // WireSock servisini yeni profille tazele
                                            var ws = FindWireSockPath();
                                            if (!string.IsNullOrEmpty(ws))
                                            {
                                                await dummyWarp.ExecuteCommandAsync("net", "stop wiresock-client-service");
                                                await Task.Delay(150);
                                                await dummyWarp.ExecuteCommandAsync(ws, $"install -start-type 2 -config \"{profFile}\" -log-level none");
                                                await dummyWarp.ExecuteCommandAsync("net", "start wiresock-client-service");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // 4) DNS ve ARP önbelleğini temizle, Cloudflare DNS tazele
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ipconfig /flushdns & arp -d * 2>nul") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
                        p?.WaitForExit(2000);
                    } catch { }

                    _ = Task.Run(async () =>
                    {
                        await SetCloudflareDnsAsync();
                        await WarmupTargetDomainsAsync();
                    });

                    logCallback?.Invoke("Discord önbelleği, mesaj veritabanı ve ağ durumu başarıyla onarıldı!");
                    return true;
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke("Onarım hatası: " + ex.Message);
                    return false;
                }
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  Komut Çalıştır (Asenkron Stream Okumalı - Deadlock Önlemeli)
        // ═══════════════════════════════════════════════════════════
        public async Task<int> ExecuteCommandAsync(string command, string arguments)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var si = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arguments,
                        WorkingDirectory = _resDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc = new Process { StartInfo = si };
                    proc.Start();
                    var outTask = proc.StandardOutput.ReadToEndAsync();
                    var errTask = proc.StandardError.ReadToEndAsync();
                    await Task.WhenAll(outTask, errTask);
                    proc.WaitForExit(15000);
                    return proc.ExitCode;
                }
                catch { return -1; }
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  Eski DPI Artıklarını ve Çakışan WinDivert Sürücülerini Temizle
        // ═══════════════════════════════════════════════════════════
        public static void PurgeDpiServices()
        {
            try
            {
                string[] procNames = { "goodbyedpi", "zapret", "winws", "windivert", "windivert64", "ciadpi", "ProxiFyre" };
                foreach (var name in procNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); } catch { }
                    }
                }

                // Çakışan WinDivert ve GoodbyeDPI servislerini durdur/kaldır
                string cmdArgs = "/c \"sc.exe stop WinDivert 2>nul & sc.exe delete WinDivert 2>nul & sc.exe stop WinDivert14 2>nul & sc.exe delete WinDivert14 2>nul & sc.exe stop WinDivert1.4 2>nul & sc.exe delete WinDivert1.4 2>nul & sc.exe stop GoodbyeDPI 2>nul & sc.exe delete GoodbyeDPI 2>nul\"";
                var psi = new ProcessStartInfo("cmd.exe", cmdArgs)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p2 = Process.Start(psi);
                p2?.WaitForExit(1000);

                // Easy Anti-Cheat ve BattlEye için temp klasörlerindeki eski sürücü/dosya kalıntılarını temizle
                try
                {
                    string[] tempFolders = {
                        Path.Combine(Path.GetTempPath(), "AlpBypass"),
                        Path.Combine(Path.GetTempPath(), "AlpNetInstall")
                    };
                    foreach (var tf in tempFolders)
                    {
                        if (!Directory.Exists(tf)) continue;
                        string[] badFiles = { "WinDivert64.sys", "WinDivert.dll", "WinDivert32.sys", "goodbyedpi.exe" };
                        foreach (var bf in badFiles)
                        {
                            var fp = Path.Combine(tf, bf);
                            if (File.Exists(fp)) { try { File.Delete(fp); } catch { } }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  WIRESOCK SERVİS DURUM VE WATCHDOG YARDIMCILARI
        // ═══════════════════════════════════════════════════════════
        public static bool IsWireSockServiceInstalled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\wiresock-client-service");
                return key != null;
            }
            catch { return false; }
        }

        public static bool IsWireSockServiceRunning()
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", "query wiresock-client-service")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(1000);
                    return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        private void StartWatchdog(Action<string>? logCallback)
        {
            _activeLogCallback = logCallback;
            StopWatchdog();
            _consecutiveFailures = 0;
            _watchdogTimer = new System.Threading.Timer(async _ => await WatchdogCheckAsync(), null, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25));
        }

        private void StopWatchdog()
        {
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
        }

        private async Task WatchdogCheckAsync()
        {
            if (!IsActive) return;

            bool healthy = await TestTunnelConnectivityAsync();
            if (healthy)
            {
                _consecutiveFailures = 0;
                return;
            }

            _consecutiveFailures++;
            Debug.WriteLine($"[Watchdog] Tunnel check failed. Consecutive failures: {_consecutiveFailures}");

            if (_consecutiveFailures >= 2)
            {
                _consecutiveFailures = 0;
                _activeLogCallback?.Invoke("🔄 Tunnel Watchdog: CGNAT / hat kesintisi algılandı, tünel otomatik yenileniyor...");
                await RefreshTunnelAsync();
            }
        }

        private async Task<bool> TestTunnelConnectivityAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(4000);
                // 1) Test Discord HTTPS through the tunnel
                var resp = await _http.GetAsync("https://discord.com", cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch { }

            try
            {
                using var cts2 = new CancellationTokenSource(3000);
                // 2) Fallback test: Cloudflare 1.1.1.1 over HTTPS
                var resp2 = await _http.GetAsync("https://1.1.1.1", cts2.Token);
                if (resp2.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch { }

            try
            {
                using var cts3 = new CancellationTokenSource(2500);
                var res = await System.Net.Dns.GetHostAddressesAsync("gateway.discord.gg", cts3.Token);
                if (res != null && res.Length > 0) return true;
            }
            catch { }

            return false;
        }

        public async Task<bool> RefreshTunnelAsync()
        {
            try
            {
                var wireSockExe = FindWireSockPath();
                var profilePath = Path.Combine(_resDir, "wgcf-profile.conf");
                if (string.IsNullOrEmpty(wireSockExe) || !File.Exists(profilePath)) return false;

                int currentIdx = Array.IndexOf(FallbackPorts, CurrentPort);
                int nextPort = FallbackPorts[(currentIdx + 1) % FallbackPorts.Length];
                if (nextPort != CurrentPort)
                {
                    _activeLogCallback?.Invoke($"🌐 Port {CurrentPort} filtrelendi, alternatif port {nextPort} deneniyor...");
                    CurrentPort = nextPort;
                    await ModifyConfigurationAsync(profilePath);
                }

                await ExecuteCommandAsync("net", "stop wiresock-client-service");
                await Task.Delay(200);
                int startRes = await ExecuteCommandAsync("net", "start wiresock-client-service");
                if (startRes != 0)
                {
                    await ExecuteCommandAsync("sc", "start wiresock-client-service");
                }

                _ = Task.Run(async () =>
                {
                    await SetCloudflareDnsAsync();
                    await WarmupTargetDomainsAsync();
                });

                _activeLogCallback?.Invoke($"✅ Tünel soketi başarıyla yenilendi (Port {CurrentPort})!");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Watchdog] Refresh failed: {ex.Message}");
                return false;
            }
        }

        private void LoadUsageToday()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string file = Path.Combine(appData, "AlpNet", "warp_usage.json");
                if (File.Exists(file))
                {
                    string json = File.ReadAllText(file);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("date", out var dElem) &&
                        dElem.GetString() == DateTime.UtcNow.ToString("yyyy-MM-dd") &&
                        doc.RootElement.TryGetProperty("totalHours", out var hElem))
                    {
                        WarpTotalHoursToday = hElem.GetDouble();
                    }
                    else
                    {
                        WarpTotalHoursToday = 0;
                    }
                }
            }
            catch { }
        }

        private void SaveUsageToday()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(appData, "AlpNet");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "warp_usage.json");
                var obj = new
                {
                    date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    totalHours = Math.Round(WarpTotalHoursToday, 2)
                };
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(obj));
            }
            catch { }
        }
    }
}
