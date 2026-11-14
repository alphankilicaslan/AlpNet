using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        // Türkiye'de 2408 UDP ISP engelli olduğundan FallbackPorts listesinden çıkarılmıştır.
        public static readonly int[] FallbackPorts = new[] { 500, 4500, 1701 };
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
            _http.DefaultRequestHeaders.Add("User-Agent", "AlpNetPulse/2.3.6");

            LoadUsageToday();
        }

        public const string CollidingSharedKey = "ID6Va+b6e5vusYdSh8PkhB+Z8gOVPZFtq2/fni+1K1Q=";

        private const string DefaultFallbackProfile = 
@"[Interface]
PrivateKey = ID6Va+b6e5vusYdSh8PkhB+Z8gOVPZFtq2/fni+1K1Q=
Address = 172.16.0.2/32, 2606:4700:110:8369:e40f:6f09:1250:1c64/128
MTU = 1280

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

                // Split tunnel yapılandırmasını doğrula (DNS = 1.1.1.1, Port 500, PersistentKeepalive=15, IPv6 ve temiz AllowedApps)
                bool configUpdated = await ModifyConfigurationAsync(profilePath);

                // Adım 3: WireSock servisini güvenli ve ultra hızlı başlat (~150ms)
                logCallback("WARP tüneli yapılandırılıyor...");
                string installArgs = $"install -start-type 2 -config \"{profilePath}\" -log-level none";

                bool isInstalled = IsWireSockServiceInstalled();
                bool isRunning = IsWireSockServiceRunning();

                if (isRunning && !configUpdated)
                {
                    logCallback("WireSock servisi devrede, tünel hazır.");
                }
                else if (isRunning && configUpdated)
                {
                    logCallback("WARP tüneli şifreli Cloudflare DNS ile yenileniyor...");
                    await ExecuteCommandAsync("net", "stop wiresock-client-service");
                    await Task.Delay(150);
                    int restartRes = await ExecuteCommandAsync("net", "start wiresock-client-service");
                    if (restartRes != 0)
                    {
                        await ExecuteCommandAsync("sc", "start wiresock-client-service");
                    }
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

                // DNS ayarla, DoH şifrelemeyi etkinleştir, HTTP/3 QUIC etkinleştir ve hedef domainleri ön ısıt (DNS warmup)
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
                _ = AlpNetTelemetryService.Instance.RecordEventAsync("WARP_CONNECTED", "WARP Split Tüneli başarıyla aktifleştirildi.", $"Port: {CurrentPort}");
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
                _ = AlpNetTelemetryService.Instance.RecordEventAsync("WARP_DISCONNECTED", "WARP Split Tüneli durduruldu.");
                StopWatchdog();
                WarpStoppedAt = DateTime.UtcNow;
                if (WarpStartedAt.HasValue)
                {
                    WarpTotalHoursToday += (DateTime.UtcNow - WarpStartedAt.Value).TotalHours;
                    SaveUsageToday();
                }

                // Hızlı durdurma (Uninstall yapmadan servisi durdurur, böylece sonraki açılış 150ms olur)
                _ = Task.Run(async () =>
                {
                    await ExecuteCommandAsync("net", "stop wiresock-client-service");
                });

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
                    // Popüler Discord Alternatif/Gelişmiş İstemcileri (Vesktop, WebCord vb.)
                    "vesktop", "Vesktop.exe",
                    "webcord", "WebCord.exe", "webcord.exe",
                    "armcord", "ArmCord.exe", "armcord.exe",
                    "legcord", "Legcord.exe", "legcord.exe",
                    "ripcord", "Ripcord.exe", "ripcord.exe",
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
                var disallowedApps = new List<string>
                {
                    "RustClient", "RustClient.exe",
                    "EasyAntiCheat", "EasyAntiCheat.exe",
                    "EasyAntiCheat_EOS", "EasyAntiCheat_EOS.exe",
                    "Steam", "Steam.exe",
                    "steamwebhelper", "steamwebhelper.exe"
                };
                var disallowedAppsLine = $"DisallowedApps = {string.Join(", ", disallowedApps.Distinct(StringComparer.OrdinalIgnoreCase))}";
                var disallowedIPsLine = "DisallowedIPs = 127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16";

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
                        trimmed.StartsWith("DisallowedIPs", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (trimmed.StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase))
                    {
                        newLines.Add("DNS = 1.1.1.1, 1.0.0.1");
                        // [Interface] bitmeden MTU eklenmediyse araya enjekte et (1280 IPv6 Safe MTU)
                        if (!mtuAdded)
                        {
                            newLines.Add("MTU = 1280");
                            mtuAdded = true;
                        }

                        inPeerSection = true;
                        newLines.Add(line);
                        continue;
                    }

                    if (trimmed.StartsWith("MTU", StringComparison.OrdinalIgnoreCase))
                    {
                        newLines.Add("MTU = 1280");
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
                            newLines.Add(disallowedAppsLine);
                                                        newLines.Add(disallowedIPsLine);
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
                    if (!allowedAppsAdded)
                    {
                        newLines.Add(allowedAppsLine);
                        newLines.Add(disallowedAppsLine);
                                                newLines.Add(disallowedIPsLine);
                    }
                }

                string oldContent = string.Join("\n", lines.Select(l => l.Trim()));
                string newContent = string.Join("\n", newLines.Select(l => l.Trim()));
                bool hasChanged = !string.Equals(oldContent, newContent, StringComparison.Ordinal);

                if (hasChanged || !File.Exists(profilePath))
                {
                    await File.WriteAllLinesAsync(profilePath, newLines);
                }

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
        public static async Task WarmupTargetDomainsAsync()
        {
            try
            {
                bool detectedPoisoning = false;
                var tasks = new List<Task>();
                foreach (var d in TargetDomains)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            using var cts = new System.Threading.CancellationTokenSource(3000);
                            var addrs = await System.Net.Dns.GetHostAddressesAsync(d, cts.Token);
                            if (addrs.Any(a => a.ToString().StartsWith("195.175.")))
                            {
                                detectedPoisoning = true;
                            }
                        }
                        catch { }
                    }));
                }
                await Task.WhenAll(tasks);

                if (detectedPoisoning)
                {
                    AlpNetTelemetryService.Instance.Warn("DNS", "ISS DNS Zehirlenmesi (195.175.x.x) algılandı! DNS önbelleği zorla temizleniyor...");
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
                    }
                    catch { }
                }
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
        public static async Task SetCloudflareDnsAsync()
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
        //  DISCORD SÜREÇ YÖNETİMİ & KONTROLÜ
        // ═══════════════════════════════════════════════════════════
        public static bool IsDiscordRunning()
        {
            try
            {
                string[] discordProcs = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment", "Vesktop", "webcord", "ArmCord", "Legcord", "Ripcord" };
                return discordProcs.Any(name =>
                {
                    try { return Process.GetProcessesByName(name).Length > 0; }
                    catch { return false; }
                });
            }
            catch
            {
                return false;
            }
        }

        public static void CloseDiscordProcesses()
        {
            string[] discordProcs = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment", "Vesktop", "webcord", "ArmCord", "Legcord", "Ripcord" };

            // 1) .NET Process.Kill(entireProcessTree: true)
            foreach (var name in discordProcs)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(true); p.WaitForExit(1500); } catch { }
                    }
                }
                catch { }
            }

            // Only kill Update.exe processes that belong to Discord or Discord clients
            try
            {
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
            }
            catch { }

            // 2) Kalan kilitli alt süreçleri zorla sonlandır
            try
            {
                var killPsi = new ProcessStartInfo("taskkill.exe", "/F /T /IM Discord.exe /IM DiscordPTB.exe /IM DiscordCanary.exe /IM DiscordDevelopment.exe /IM Vesktop.exe /IM webcord.exe /IM ArmCord.exe /IM Legcord.exe /IM Ripcord.exe")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var pKill = Process.Start(killPsi);
                pKill?.WaitForExit(2000);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  DISCORD ÖNBELLEK VE BAĞLANTI ONARIMI (1 TIKLA OTOMATİK ÇÖZÜM)
        // ═══════════════════════════════════════════════════════════
        public static async Task<bool> RepairDiscordCacheAsync(Action<string>? logCallback = null, bool autoRelaunch = true)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    bool wasRunning = IsDiscordRunning();
                    logCallback?.Invoke("Discord ve istemci süreçleri güvenle kapatılıyor...");
                    CloseDiscordProcesses();

                    for (int i = 0; i < 20 && IsDiscordRunning(); i++)
                    {
                        await Task.Delay(100);
                    }

                    logCallback?.Invoke("Tüm kullanıcı profillerindeki bozuk Discord soketleri ve QUIC önbellekleri taranıyor...");

                    // 3) TÜM KULLANICI PROFİLLERİNİ TARA (Çoklu profil veya Admin oturumu desteği)
                    var userProfiles = new List<(string Roaming, string Local)>();

                    // Mevcut çalışan kullanıcının dizinleri
                    try
                    {
                        var curRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        var curLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        if (!string.IsNullOrEmpty(curRoaming) && !string.IsNullOrEmpty(curLocal))
                            userProfiles.Add((curRoaming, curLocal));
                    }
                    catch { }

                    // C:\Users altındaki tüm kullanıcıları tara
                    try
                    {
                        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                        string usersDir = Path.Combine(systemDrive, "Users");
                        if (Directory.Exists(usersDir))
                        {
                            foreach (var uDir in Directory.GetDirectories(usersDir))
                            {
                                string uName = Path.GetFileName(uDir);
                                if (uName.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                                    uName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                                    uName.Equals("All Users", StringComparison.OrdinalIgnoreCase) ||
                                    uName.Equals("Default User", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string r = Path.Combine(uDir, "AppData", "Roaming");
                                string l = Path.Combine(uDir, "AppData", "Local");
                                if (Directory.Exists(r) || Directory.Exists(l))
                                {
                                    if (!userProfiles.Any(p => string.Equals(p.Roaming, r, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        userProfiles.Add((r, l));
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    string[] discordDirs = { "discord", "discordptb", "discordcanary", "discorddevelopment", "vesktop", "webcord", "ArmCord", "Legcord", "Ripcord" };

                    foreach (var (rDir, lDir) in userProfiles)
                    {
                        foreach (var d in discordDirs)
                        {
                            // A) Roaming AppData (Chromium Network, Alt-Svc bozuk QUIC soketleri ve önbellekler)
                            var fullDir = Path.Combine(rDir, d);
                            if (Directory.Exists(fullDir))
                            {
                                // 1) Chromium Network State ve Alt-Svc / HTTP/3 QUIC bozuk durum dosyalarını sıfırla
                                var networkDir = Path.Combine(fullDir, "Network");
                                if (Directory.Exists(networkDir))
                                {
                                    string[] networkFiles = { 
                                        "Network Persistent State", "Network Persistent State.tmp", 
                                        "TransportSecurity", "Trust Tokens", "Trust Tokens-journal", 
                                        "Reporting and NEL", "Reporting and NEL-journal" 
                                    };
                                    foreach (var nf in networkFiles)
                                    {
                                        SafeDeleteFile(Path.Combine(networkDir, nf));
                                    }
                                    try
                                    {
                                        foreach (var tmpFile in Directory.GetFiles(networkDir, "*.tmp"))
                                        {
                                            SafeDeleteFile(tmpFile);
                                        }
                                    }
                                    catch { }
                                }

                                // 2) Bozuk mesaj veritabanı, GPU ve Chromium önbelleklerini temizle (Local Storage korunur, oturum düşmez)
                                string[] cacheFolders = { 
                                    "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", 
                                    "blob_storage", "Session Storage", "IndexedDB", "Service Worker", "Shared Dictionary", 
                                    "shared_proto_db", "WebStorage", "VideoDecodeStats", "Crashpad",
                                    Path.Combine("Network", "Cache"), Path.Combine("Network", "Code Cache") 
                                };
                                foreach (var cf in cacheFolders)
                                {
                                    var target = Path.Combine(fullDir, cf);
                                    if (Directory.Exists(target))
                                    {
                                        try { Directory.Delete(target, true); } catch { }
                                    }
                                }

                                // 3) Kilit dosyalarını sil
                                var lockPath = Path.Combine(fullDir, "lockfile");
                                if (File.Exists(lockPath)) { try { File.Delete(lockPath); } catch { } }

                                // 4) CRITICAL FIX: Discord'un kilitlenen HTTP/3 (QUIC) protokolünü settings.json içinde devre dışı bırak
                                // Bu işlem mesajların sunucudan gecikmesiz ve hatasız (TCP HTTP/2) yüklenmesini garanti eder!
                                try
                                {
                                    string settingsFile = Path.Combine(fullDir, "settings.json");
                                    JsonNode? rootNode = null;
                                    if (File.Exists(settingsFile))
                                    {
                                        try
                                        {
                                            string jsonText = File.ReadAllText(settingsFile);
                                            rootNode = JsonNode.Parse(jsonText);
                                        }
                                        catch { }
                                    }

                                    rootNode ??= new JsonObject();

                                    JsonObject switchesObj;
                                    if (rootNode["chromiumSwitches"] is JsonObject existingSwitches)
                                    {
                                        switchesObj = existingSwitches;
                                    }
                                    else
                                    {
                                        switchesObj = new JsonObject();
                                        rootNode["chromiumSwitches"] = switchesObj;
                                    }

                                    // QUIC'i devre dışı bırak
                                    switchesObj["disable-quic"] = "true";

                                    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                                    File.WriteAllText(settingsFile, rootNode.ToJsonString(jsonOptions));
                                }
                                catch { }
                            }

                            // B) Local AppData önbellekleri
                            if (!string.IsNullOrEmpty(lDir) && Directory.Exists(lDir))
                            {
                                var locDir = Path.Combine(lDir, d);
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
                        }
                    }

                    // 4) Çakışan ortak anahtarlı profil kontrolü ve cihaza özel benzersiz kimlik onarımı
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

                    // 5) WARP tüneli aktifse WireSock servisini ve soket kancalarını sıfırla (Kapa-Aç)
                    try
                    {
                        if (IsWireSockServiceRunning())
                        {
                            logCallback?.Invoke("WARP tüneli ve ağ soketleri sıfırlanıyor (Kapa-Aç)...");
                            var dummyWarp = new WarpManager();
                            await dummyWarp.ExecuteCommandAsync("net", "stop wiresock-client-service");
                            await Task.Delay(250);
                            int startRes = await dummyWarp.ExecuteCommandAsync("net", "start wiresock-client-service");
                            if (startRes != 0)
                            {
                                await dummyWarp.ExecuteCommandAsync("sc", "start wiresock-client-service");
                            }
                        }
                    }
                    catch { }

                    // 6) DNS ve ARP önbelleğini temizle, Cloudflare DNS tazele
                    try
                    {
                        using var p1 = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false }); 
                        p1?.WaitForExit(1000); 
                        using var p2 = Process.Start(new ProcessStartInfo("arp.exe", "-d *") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false }); 
                        p2?.WaitForExit(1000);
                    } 
                    catch { }

                    _ = Task.Run(async () =>
                    {
                        await SetCloudflareDnsAsync();
                        await WarmupTargetDomainsAsync();
                    });

                    logCallback?.Invoke("Discord önbelleği, mesaj soketleri ve QUIC kilitlenmeleri başarıyla onarıldı!");

                    if (autoRelaunch || wasRunning)
                    {
                        logCallback?.Invoke("Discord HTTP/2 güvenli modunda (--disable-quic) yeniden başlatılıyor...");
                        await Task.Delay(300);
                        LaunchDiscord();
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke("Onarım hatası: " + ex.Message);
                    return false;
                }
            });
        }

        private static void SafeDeleteFile(string path)
        {
            if (!File.Exists(path)) return;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(60);
                }
                catch
                {
                    return;
                }
            }
        }

        public static void EnsureDiscordShortcutsDisableQuic()
        {
            try
            {
                var shortcutPaths = new List<string>();
                var searchRoots = new List<string>();

                try
                {
                    string curDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    if (!string.IsNullOrEmpty(curDesktop)) searchRoots.Add(curDesktop);
                    string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                    if (!string.IsNullOrEmpty(commonDesktop)) searchRoots.Add(commonDesktop);
                    string curStart = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                    if (!string.IsNullOrEmpty(curStart)) searchRoots.Add(curStart);
                    string commonStart = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
                    if (!string.IsNullOrEmpty(commonStart)) searchRoots.Add(commonStart);
                    string curProg = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    if (!string.IsNullOrEmpty(curProg)) searchRoots.Add(curProg);
                    string commonProg = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
                    if (!string.IsNullOrEmpty(commonProg)) searchRoots.Add(commonProg);
                }
                catch { }

                try
                {
                    string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                    string usersDir = Path.Combine(systemDrive, "Users");
                    if (Directory.Exists(usersDir))
                    {
                        foreach (var u in Directory.GetDirectories(usersDir))
                        {
                            var d = Path.Combine(u, "Desktop");
                            var s = Path.Combine(u, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu");
                            if (Directory.Exists(d)) searchRoots.Add(d);
                            if (Directory.Exists(s)) searchRoots.Add(s);
                        }
                    }
                }
                catch { }

                foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root)) continue;
                    try
                    {
                        foreach (var lnk in Directory.GetFiles(root, "*discord*.lnk", SearchOption.AllDirectories))
                        {
                            shortcutPaths.Add(lnk);
                        }
                    }
                    catch { }
                }

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType)!;

                foreach (var lnkPath in shortcutPaths)
                {
                    try
                    {
                        dynamic shortcut = shell.CreateShortcut(lnkPath);
                        string target = shortcut.TargetPath as string ?? "";
                        string args = shortcut.Arguments as string ?? "";

                        if (target.IndexOf("Discord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            target.IndexOf("Update.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!args.Contains("--disable-quic", StringComparison.OrdinalIgnoreCase))
                            {
                                if (target.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase) || args.Contains("--processStart"))
                                {
                                    if (args.Contains("--process-start-args"))
                                    {
                                        shortcut.Arguments = args + " --disable-quic";
                                    }
                                    else
                                    {
                                        shortcut.Arguments = (args + " --process-start-args --disable-quic").Trim();
                                    }
                                }
                                else
                                {
                                    shortcut.Arguments = (args + " --disable-quic").Trim();
                                }
                                shortcut.Save();
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  DISCORD BAŞLATMA YARDIMCISI (En Güncel Kurulu Sürümü Başlat)
        // ═══════════════════════════════════════════════════════════
        public static void LaunchDiscord()
        {
            try
            {
                EnsureDiscordShortcutsDisableQuic();

                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                string usersDir = Path.Combine(systemDrive, "Users");
                var allLocals = new List<string>();

                try
                {
                    var curLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(curLocal)) allLocals.Add(curLocal);
                }
                catch { }

                if (Directory.Exists(usersDir))
                {
                    foreach (var uDir in Directory.GetDirectories(usersDir))
                    {
                        var l = Path.Combine(uDir, "AppData", "Local");
                        if (Directory.Exists(l) && !allLocals.Contains(l, StringComparer.OrdinalIgnoreCase))
                        {
                            allLocals.Add(l);
                        }
                    }
                }

                string? targetExe = null;
                string? targetDir = null;

                string[] clientFolders = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment", "Vesktop", "webcord", "ArmCord", "Legcord", "Ripcord" };

                foreach (var l in allLocals)
                {
                    foreach (var client in clientFolders)
                    {
                        string[] candidateDirs = {
                            Path.Combine(l, client),
                            Path.Combine(l, "Programs", client)
                        };

                        foreach (var discDir in candidateDirs)
                        {
                            if (!Directory.Exists(discDir)) continue;

                            // 1) Öncelik: Discord resmi Squirrel Update.exe başlatıcısı
                            // app-* altındaki Discord.exe doğrudan başlatıldığında Squirrel kancası olmadığı için kapanabilir;
                            // Update.exe üzerinden başlatmak hem pencereyi garantili açar hem de --disable-quic bayrağını korur.
                            var updateExe = Path.Combine(discDir, "Update.exe");
                            if (File.Exists(updateExe))
                            {
                                string targetClientExe = $"{client}.exe";
                                var psiUpdate = new ProcessStartInfo
                                {
                                    FileName = updateExe,
                                    Arguments = $"--processStart {targetClientExe} --process-start-args \"--disable-quic\"",
                                    WorkingDirectory = discDir,
                                    UseShellExecute = true
                                };
                                Process.Start(psiUpdate);
                                return;
                            }

                            var appDirs = Directory.GetDirectories(discDir, "app-*")
                                .OrderByDescending(d =>
                                {
                                    string folder = Path.GetFileName(d);
                                    if (folder.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                                        folder = folder.Substring(4);
                                    return Version.TryParse(folder, out var v) ? v : new Version(0, 0, 0);
                                })
                                .ThenByDescending(d => d)
                                .ToList();

                            foreach (var ad in appDirs)
                            {
                                var exe = Path.Combine(ad, $"{client}.exe");
                                if (!File.Exists(exe)) exe = Path.Combine(ad, "Discord.exe");
                                if (File.Exists(exe))
                                {
                                    targetExe = exe;
                                    targetDir = ad;
                                    break;
                                }
                            }
                            if (targetExe != null) break;

                            var rootExe = Path.Combine(discDir, $"{client}.exe");
                            if (!File.Exists(rootExe)) rootExe = Path.Combine(discDir, "Discord.exe");
                            if (File.Exists(rootExe))
                            {
                                targetExe = rootExe;
                                targetDir = discDir;
                                break;
                            }
                        }
                        if (targetExe != null) break;
                    }
                    if (targetExe != null) break;
                }

                // Fallback: Check Program Files and Program Files (x86)
                if (string.IsNullOrEmpty(targetExe))
                {
                    string[] programFilesRoots = {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                    };

                    foreach (var pRoot in programFilesRoots)
                    {
                        if (string.IsNullOrEmpty(pRoot) || !Directory.Exists(pRoot)) continue;
                        foreach (var client in clientFolders)
                        {
                            var dDir = Path.Combine(pRoot, client);
                            if (!Directory.Exists(dDir)) continue;
                            var exe = Path.Combine(dDir, $"{client}.exe");
                            if (!File.Exists(exe)) exe = Path.Combine(dDir, "Discord.exe");
                            if (File.Exists(exe))
                            {
                                targetExe = exe;
                                targetDir = dDir;
                                break;
                            }
                        }
                        if (targetExe != null) break;
                    }
                }

                if (!string.IsNullOrEmpty(targetExe) && !string.IsNullOrEmpty(targetDir))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = targetExe,
                        Arguments = "--disable-quic",
                        WorkingDirectory = targetDir,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  SESSİZ DİSCORD AĞ KORUMASI (AĞ KARTI GÜNCELLEME SONRASI DOĞRULAMA)
        // ═══════════════════════════════════════════════════════════
        public static void RepairDiscordNetworkStateSilent(bool triggerRestartIfRunning = false)
        {
            try
            {
                EnsureDiscordShortcutsDisableQuic();

                bool wasRunning = IsDiscordRunning();
                if (wasRunning && triggerRestartIfRunning)
                {
                    CloseDiscordProcesses();
                    for (int i = 0; i < 20 && IsDiscordRunning(); i++)
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                }

                bool isCurrentlyRunning = IsDiscordRunning();

                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                string usersDir = Path.Combine(systemDrive, "Users");
                var userProfiles = new List<string>();

                try
                {
                    var curRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    if (!string.IsNullOrEmpty(curRoaming)) userProfiles.Add(curRoaming);
                }
                catch { }

                if (Directory.Exists(usersDir))
                {
                    foreach (var uDir in Directory.GetDirectories(usersDir))
                    {
                        string uName = Path.GetFileName(uDir);
                        if (uName.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                            uName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                            uName.Equals("All Users", StringComparison.OrdinalIgnoreCase) ||
                            uName.Equals("Default User", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string r = Path.Combine(uDir, "AppData", "Roaming");
                        if (Directory.Exists(r) && !userProfiles.Contains(r, StringComparer.OrdinalIgnoreCase))
                        {
                            userProfiles.Add(r);
                        }
                    }
                }

                string[] discordDirs = { "discord", "discordptb", "discordcanary", "discorddevelopment", "vesktop", "webcord", "ArmCord", "Legcord", "Ripcord" };

                foreach (var rDir in userProfiles)
                {
                    foreach (var d in discordDirs)
                    {
                        var fullDir = Path.Combine(rDir, d);
                        if (!Directory.Exists(fullDir)) continue;

                        // 1) QUIC'i settings.json içinde kalıcı olarak devre dışı bırak
                        try
                        {
                            string settingsFile = Path.Combine(fullDir, "settings.json");
                            JsonNode? rootNode = null;
                            if (File.Exists(settingsFile))
                            {
                                try { rootNode = JsonNode.Parse(File.ReadAllText(settingsFile)); } catch { }
                            }
                            rootNode ??= new JsonObject();
                            JsonObject switchesObj;
                            if (rootNode["chromiumSwitches"] is JsonObject existingSwitches)
                            {
                                switchesObj = existingSwitches;
                            }
                            else
                            {
                                switchesObj = new JsonObject();
                                rootNode["chromiumSwitches"] = switchesObj;
                            }
                            switchesObj["disable-quic"] = "true";
                            File.WriteAllText(settingsFile, rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        }
                        catch { }

                        // 2) Ağ kartı geçişinde kilitlenen bozuk QUIC socket ve state dosyalarını sil
                        // NOT: Eğer Discord çalışıyorsa kilitli dosyaları silmeye çalışma; yalnızca Discord kapalıyken sil!
                        if (!isCurrentlyRunning)
                        {
                            var networkDir = Path.Combine(fullDir, "Network");
                            if (Directory.Exists(networkDir))
                            {
                                string[] filesToClean = { 
                                    "Network Persistent State", "Network Persistent State.tmp", 
                                    "TransportSecurity", "Trust Tokens", "Trust Tokens-journal", 
                                    "Reporting and NEL", "Reporting and NEL-journal" 
                                };
                                foreach (var f in filesToClean)
                                {
                                    SafeDeleteFile(Path.Combine(networkDir, f));
                                }
                                try
                                {
                                    foreach (var tmpFile in Directory.GetFiles(networkDir, "*.tmp"))
                                    {
                                        SafeDeleteFile(tmpFile);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }

                if (wasRunning && triggerRestartIfRunning)
                {
                    LaunchDiscord();
                }
            }
            catch { }
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
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                    ChildProcessTracker.AddProcess(proc);
                    var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                    var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
                    var exitTask = proc.WaitForExitAsync(cts.Token);
                    await Task.WhenAll(outTask, errTask, exitTask);
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


                // Çakışan WinDivert ve GoodbyeDPI servislerini sessiz ve güvenli kaldır (Defender uyarısını önler)
                string[] services = { "WinDivert", "WinDivert14", "WinDivert1.4", "GoodbyeDPI" };
                foreach (var s in services)
                {
                    try
                    {
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s);
                        if (key != null)
                        {
                            using var scStop = Process.Start(new ProcessStartInfo("sc.exe", $"stop {s}") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                            scStop?.WaitForExit(500);
                            using var scDel = Process.Start(new ProcessStartInfo("sc.exe", $"delete {s}") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                            scDel?.WaitForExit(500);
                        }
                    }
                    catch { }
                }

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

            if (_consecutiveFailures >= 3)
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
                using var cts = new CancellationTokenSource(7000);
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
                using var cts2 = new CancellationTokenSource(5000);
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
                using var cts3 = new CancellationTokenSource(4000);
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
                await Task.Delay(600);
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
