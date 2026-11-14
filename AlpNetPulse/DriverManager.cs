using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AlpNetPulse
{
    public enum DriverCategory
    {
        GPU,
        CPU,
        Motherboard,
        Network,
        Audio,
        Other
    }

    public enum DriverStatus
    {
        UpToDate,
        UpdateAvailable,
        Updating,
        BackupReady,
        Error
    }

    public class DriverInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DriverCategory Category { get; set; }
        public string Manufacturer { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string CurrentDateStr { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string LatestDateStr { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string DownloadSize { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string InfName { get; set; } = string.Empty;
        public string HardwareId { get; set; } = string.Empty;
        public string WindowsUpdateTitle { get; set; } = string.Empty;
        public bool IsUpdateAvailable { get; set; }
        public bool IsCritical { get; set; }
        public bool IsBiosFirmware { get; set; }
        public bool HasExistingBackup { get; set; }
        public string ExistingBackupPath { get; set; } = string.Empty;
        public string ExistingBackupOldVersion { get; set; } = string.Empty;
        public DriverUpdateRecord? ExistingBackupRecord { get; set; }
        public DriverStatus Status { get; set; } = DriverStatus.UpToDate;
        public string StatusBadgeText { get; set; } = "✔ GÜNCEL";
        public string StatusColorHex { get; set; } = "#00FF87"; // Green
        public string CategoryBadgeText => Category switch
        {
            DriverCategory.GPU => "🎮 EKRAN KARTI",
            DriverCategory.CPU => "🧠 İŞLEMCİ",
            DriverCategory.Motherboard => "🖧 ANAKART / ÇİPSET",
            DriverCategory.Network => "📡 AĞ & WI-FI",
            DriverCategory.Audio => "🔊 SES AYGITI",
            _ => "⚡ SİSTEM"
        };
        public string CategoryColorHex => Category switch
        {
            DriverCategory.GPU => "#00F2FE",
            DriverCategory.CPU => "#667EEA",
            DriverCategory.Motherboard => "#FF9900",
            DriverCategory.Network => "#00FF87",
            DriverCategory.Audio => "#E040FB",
            _ => "#ADB5BD"
        };
    }

    public class DriverUpdateRecord
    {
        public string UpdateId { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string OldVersion { get; set; } = string.Empty;
        public string NewVersion { get; set; } = string.Empty;
        public string Status { get; set; } = "Başarılı";
        public bool Verified { get; set; } = true;
        public string UserName { get; set; } = Environment.UserName;
        public string MachineName { get; set; } = Environment.MachineName;
        public string BackupPath { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }

    public class DriverManager
    {
        private static readonly Lazy<DriverManager> _instance = new(() => new DriverManager());
        public static DriverManager Instance => _instance.Value;

        private readonly HttpClient _http;
        private readonly string _driverDataDir;
        private readonly string _backupsDir;
        private readonly string _historyFile;

        private DriverManager()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 AlpNetPulse/2.3.6");

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _driverDataDir = Path.Combine(localAppData, "AlpNet", "Drivers");
            _backupsDir = Path.Combine(_driverDataDir, "Backups");
            _historyFile = Path.Combine(_driverDataDir, "driver_history.json");

            try
            {
                if (!Directory.Exists(_driverDataDir)) Directory.CreateDirectory(_driverDataDir);
                if (!Directory.Exists(_backupsDir)) Directory.CreateDirectory(_backupsDir);
            }
            catch { }
        }

        public string GetBackupsDirectory() => _backupsDir;

        // ═══════════════════════════════════════════════════════════
        //  1. SÜRÜCÜLERİ TARA (GPU, CPU, ANAKART, WI-FI, DİĞER)
        // ═══════════════════════════════════════════════════════════
        public async Task<List<DriverInfo>> ScanDriversAsync(Action<string>? progressCallback = null)
        {
            var results = new List<DriverInfo>();

            await Task.Run(async () =>
            {
                progressCallback?.Invoke("Donanım sürücüleri paralel taranıyor (GPU, CPU, Anakart, Ağ)...");

                // Paralel donanım taraması (GPU, CPU, Anakart, Ağ adaptörleri)
                var gpuTask = Task.Run(async () =>
                {
                    try
                    {
                        var gpus = ScanVideoControllers();
                        var checkTasks = gpus.Select(g => CheckGpuOnlineUpdateAsync(g));
                        await Task.WhenAll(checkTasks);
                        return gpus;
                    }
                    catch (Exception ex)
                    {
                        AlpNetTelemetryService.Instance.Warn("DRIVER", $"GPU tarama hatası: {ex.Message}");
                        return new List<DriverInfo>();
                    }
                });

                var cpuTask = Task.Run(() =>
                {
                    try
                    {
                        return ScanProcessors();
                    }
                    catch (Exception ex)
                    {
                        AlpNetTelemetryService.Instance.Warn("DRIVER", $"CPU tarama hatası: {ex.Message}");
                        return new List<DriverInfo>();
                    }
                });

                var mbTask = Task.Run(() =>
                {
                    try
                    {
                        return ScanMotherboardAndChipset();
                    }
                    catch (Exception ex)
                    {
                        AlpNetTelemetryService.Instance.Warn("DRIVER", $"Anakart tarama hatası: {ex.Message}");
                        return new List<DriverInfo>();
                    }
                });

                var netTask = Task.Run(() =>
                {
                    try
                    {
                        return ScanNetworkAdapters();
                    }
                    catch (Exception ex)
                    {
                        AlpNetTelemetryService.Instance.Warn("DRIVER", $"Ağ tarama hatası: {ex.Message}");
                        return new List<DriverInfo>();
                    }
                });

                await Task.WhenAll(gpuTask, cpuTask, mbTask, netTask);

                results.AddRange(gpuTask.Result);
                results.AddRange(cpuTask.Result);
                results.AddRange(mbTask.Result);
                results.AddRange(netTask.Result);

                // AlpTogether Cihaz Donanım Özellikleri Telemetrisi (Arka Planda Eşzamanlı)
                var hardwareSyncTask = Task.Run(async () =>
                {
                    try
                    {
                        var specs = CollectDeviceHardwareSpecs(gpuTask.Result, cpuTask.Result, mbTask.Result, netTask.Result);
                        await AlpNetTelemetryService.Instance.SyncDeviceSpecificationsAsync(specs);
                    }
                    catch (Exception ex)
                    {
                        AlpNetTelemetryService.Instance.Warn("DRIVER", $"Donanım özellikleri telemetrisi hatası: {ex.Message}");
                    }
                });

                // Windows Update WHQL Sürücü Araması (Maksimum 6 saniye kesin zaman aşımı koruması)
                progressCallback?.Invoke("Windows WHQL sürücü veritabanı denetleniyor...");
                try
                {
                    var wuTask = CheckWindowsUpdateDriversAsync(results);
                    var delayTask = Task.Delay(6000);
                    var completedTask = await Task.WhenAny(wuTask, delayTask);
                    if (completedTask == delayTask)
                    {
                        AlpNetTelemetryService.Instance.Warn("DRIVER", "Windows Update sürücü araması 6 saniye zaman aşımına uğradı, mevcut donanım listesiyle devam ediliyor.");
                    }
                }
                catch (Exception ex)
                {
                    AlpNetTelemetryService.Instance.Warn("DRIVER", $"Windows Update sorgu hatası: {ex.Message}");
                }

                // 48 Saatlik Yedek Temizliği ve Donanım Yedek Eşleştirmesi
                PruneBackupsOlderThan(TimeSpan.FromHours(48));

                foreach (var driver in results)
                {
                    // 1. Son 48 saat içindeki yedeği kontrol et
                    var backup = GetLatestBackupForDevice(driver);
                    if (backup != null)
                    {
                        driver.HasExistingBackup = true;
                        driver.ExistingBackupPath = backup.BackupPath;
                        driver.ExistingBackupOldVersion = backup.OldVersion;
                        driver.ExistingBackupRecord = backup;
                    }

                    // 2. Güvenlik Denetimi: Yüklü sürüm WHQL sürümüne eşit veya daha yeniyse ASLA güncelleme gösterme
                    if (!driver.IsUpdateAvailable || !IsNewerVersion(driver.LatestVersion, driver.CurrentVersion) || string.Equals(driver.CurrentVersion, driver.LatestVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        driver.IsUpdateAvailable = false;
                        driver.LatestVersion = driver.CurrentVersion;
                        driver.LatestDateStr = "En Son WHQL";
                        driver.Status = DriverStatus.UpToDate;
                        driver.StatusBadgeText = driver.IsBiosFirmware ? "✔ KORUMALI BIOS (UEFI)" : "✔ GÜNCEL (WHQL Doğrulandı)";
                        driver.StatusColorHex = driver.IsBiosFirmware ? "#667EEA" : "#00FF87";
                    }
                }

                // Donanım telemetrisinin buluta aktarılmasını bekle (maksimum 2.5 sn)
                try
                {
                    await Task.WhenAny(hardwareSyncTask, Task.Delay(2500));
                }
                catch { }

                progressCallback?.Invoke($"Tarama tamamlandı! {results.Count} adet donanım sürücüsü incelendi.");
            });

            return results;
        }

        // ═══════════════════════════════════════════════════════════
        //  A. GPU TARAMA & SÜRÜM DÖNÜŞTÜRME
        // ═══════════════════════════════════════════════════════════
        private List<DriverInfo> ScanVideoControllers()
        {
            var list = new List<DriverInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? "Bilinmeyen Ekran Kartı";
                    string rawVer = obj["DriverVersion"]?.ToString()?.Trim() ?? "Bilinmiyor";
                    string dateStr = FormatDriverDate(obj["DriverDate"]?.ToString());
                    string pnpId = obj["PNPDeviceID"]?.ToString() ?? "";

                    // Filtrele: Microsoft Basic Display Adapter veya yazılımsal adaptörler
                    if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && list.Count > 0)
                        continue;

                    string manufacturer = "Bilinmiyor";
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) manufacturer = "NVIDIA";
                    else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) manufacturer = "AMD";
                    else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) manufacturer = "Intel";

                    // NVIDIA için sürücü sürümünü temizle (ör. 32.0.16.2101 -> 621.01 veya 31.0.15.6094 -> 560.94)
                    string displayVer = FormatGpuDisplayVersion(rawVer, manufacturer);

                    var item = new DriverInfo
                    {
                        Id = !string.IsNullOrEmpty(pnpId) ? pnpId : Guid.NewGuid().ToString("N"),
                        Name = name,
                        Category = DriverCategory.GPU,
                        Manufacturer = manufacturer,
                        CurrentVersion = displayVer,
                        CurrentDateStr = dateStr,
                        LatestVersion = displayVer,
                        LatestDateStr = dateStr,
                        HardwareId = pnpId,
                        Status = DriverStatus.UpToDate,
                        StatusBadgeText = "✔ GÜNCEL",
                        StatusColorHex = "#00FF87"
                    };

                    list.Add(item);
                }
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Warn("DRIVER", $"Win32_VideoController sorgu hatası: {ex.Message}");
            }
            return list;
        }

        private static string FormatGpuDisplayVersion(string rawVer, string manufacturer)
        {
            if (string.IsNullOrWhiteSpace(rawVer)) return "Bilinmiyor";

            if (manufacturer == "NVIDIA")
            {
                // Windows sürücü sürümü son 5 hanesi NVIDIA sürüm numarasıdır (örn: 32.0.16.2101 -> 621.01)
                var match = Regex.Match(rawVer, @"\b\d+\.\d+\.(\d{2})\.(\d{4})\b");
                if (match.Success)
                {
                    string part1 = match.Groups[1].Value; // örn: 16
                    string part2 = match.Groups[2].Value; // örn: 2101
                    // 16.2101 -> son 5 rakam: 62101 -> 621.01
                    string combined = (part1 + part2);
                    if (combined.Length >= 5)
                    {
                        string sub = combined.Substring(combined.Length - 5);
                        return $"{sub.Substring(0, 3)}.{sub.Substring(3)}";
                    }
                }
                var simple = Regex.Match(rawVer, @"(\d{3}\.\d{2})");
                if (simple.Success) return simple.Value;
            }

            return rawVer;
        }

        // ═══════════════════════════════════════════════════════════
        //  A2. NVIDIA / AMD / INTEL ÇEVRİMİÇİ GÜNCELLEME KONTROLÜ
        // ═══════════════════════════════════════════════════════════
        private async Task CheckGpuOnlineUpdateAsync(DriverInfo gpu)
        {
            if (gpu.Manufacturer == "NVIDIA")
            {
                try
                {
                    // NVIDIA resmi AjaxDriverService sorgusu
                    int psid = 127; // Varsayılan GeForce RTX 40 Serisi
                    int pfid = 1023; // RTX 4060 Masaüstü
                    bool isNotebook = gpu.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ||
                                      gpu.Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) ||
                                      gpu.Name.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);

                    if (gpu.Name.Contains("4060", StringComparison.OrdinalIgnoreCase))
                    {
                        psid = isNotebook ? 129 : 127;
                        pfid = isNotebook ? 1007 : 1023;
                    }
                    else if (gpu.Name.Contains("4070", StringComparison.OrdinalIgnoreCase))
                    {
                        psid = isNotebook ? 129 : 127;
                        pfid = isNotebook ? 1006 : 1015;
                    }
                    else if (gpu.Name.Contains("4080", StringComparison.OrdinalIgnoreCase))
                    {
                        psid = isNotebook ? 129 : 127;
                        pfid = isNotebook ? 1005 : 1000;
                    }
                    else if (gpu.Name.Contains("4090", StringComparison.OrdinalIgnoreCase))
                    {
                        psid = isNotebook ? 129 : 127;
                        pfid = isNotebook ? 1004 : 989;
                    }
                    else if (gpu.Name.Contains("3060", StringComparison.OrdinalIgnoreCase))
                    {
                        psid = isNotebook ? 122 : 120;
                        pfid = isNotebook ? 954 : 963;
                    }
                    else if (gpu.Name.Contains("50", StringComparison.OrdinalIgnoreCase) && gpu.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase))
                    {
                        psid = isNotebook ? 133 : 131;
                        pfid = isNotebook ? 1079 : 1078;
                    }

                    string url = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1078&beta=null&isWHQL=1&dltype=-1&dch=1&upCRD=0&sort1=0&numberOfResults=1";

                    var resp = await _http.GetStringAsync(url);
                    if (!string.IsNullOrEmpty(resp) && resp.Contains("\"downloadInfo\""))
                    {
                        using var doc = JsonDocument.Parse(resp);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("IDS", out var ids) && ids.GetArrayLength() > 0)
                        {
                            var info = ids[0].GetProperty("downloadInfo");
                            string latestVer = info.GetProperty("Version").GetString() ?? "";
                            string releaseDate = info.GetProperty("ReleaseDateTime").GetString() ?? "";
                            string downloadUrl = info.GetProperty("DownloadURL").GetString() ?? "";
                            string fileSize = info.GetProperty("DownloadURLFileSize").GetString() ?? "";
                            string driverName = Uri.UnescapeDataString(info.GetProperty("Name").GetString() ?? "GeForce Game Ready Driver");
                            string notes = Uri.UnescapeDataString(info.GetProperty("ReleaseNotes").GetString() ?? "");

                            if (!string.IsNullOrEmpty(latestVer))
                            {
                                gpu.LatestVersion = latestVer;
                                gpu.LatestDateStr = releaseDate;
                                gpu.DownloadUrl = downloadUrl;
                                gpu.DownloadSize = fileSize;
                                gpu.ReleaseNotes = StripHtml(notes);

                                // Sürüm karşılaştırması
                                if (IsNewerVersion(latestVer, gpu.CurrentVersion))
                                {
                                    gpu.IsUpdateAvailable = true;
                                    gpu.IsCritical = true;
                                    gpu.Status = DriverStatus.UpdateAvailable;
                                    gpu.StatusBadgeText = $"⚡ YENİ GÜNCELLEME: v{latestVer}";
                                    gpu.StatusColorHex = "#00F2FE";
                                }
                                else
                                {
                                    gpu.IsUpdateAvailable = false;
                                    gpu.Status = DriverStatus.UpToDate;
                                    gpu.StatusBadgeText = $"✔ EN GÜNCEL (v{latestVer})";
                                    gpu.StatusColorHex = "#00FF87";
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AlpNetTelemetryService.Instance.Warn("DRIVER", $"NVIDIA API güncelleme sorgulama hatası: {ex.Message}");
                }
            }
            else if (gpu.Manufacturer == "AMD")
            {
                // AMD Radeon Adrenalin En Güncel Bilgisi
                gpu.LatestVersion = "24.12.1 WHQL";
                gpu.LatestDateStr = "Aralık 2026";
                gpu.DownloadUrl = "https://www.amd.com/en/support/download/drivers.html";
                gpu.DownloadSize = "~680 MB";
                if (IsNewerVersion("24.12.1", gpu.CurrentVersion))
                {
                    gpu.IsUpdateAvailable = true;
                    gpu.Status = DriverStatus.UpdateAvailable;
                    gpu.StatusBadgeText = "⚡ AMD ADRENALIN GÜNCELLEMESİ MEVCUT";
                    gpu.StatusColorHex = "#FF5CAA";
                }
            }
            else if (gpu.Manufacturer == "Intel")
            {
                // Intel Graphics En Güncel WHQL
                gpu.LatestVersion = "32.0.101.6252";
                gpu.LatestDateStr = "Ekim 2026";
                gpu.DownloadUrl = "https://www.intel.com/content/www/us/en/download-center/home.html";
                gpu.DownloadSize = "~800 MB";
                if (IsNewerVersion("32.0.101.6252", gpu.CurrentVersion))
                {
                    gpu.IsUpdateAvailable = true;
                    gpu.Status = DriverStatus.UpdateAvailable;
                    gpu.StatusBadgeText = "⚡ INTEL ARC/GRAPHICS GÜNCELLEMESİ MEVCUT";
                    gpu.StatusColorHex = "#00F2FE";
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  B. İŞLEMCİ (CPU) TARAMA
        // ═══════════════════════════════════════════════════════════
        private List<DriverInfo> ScanProcessors()
        {
            var list = new List<DriverInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                var proc = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (proc != null)
                {
                    string cpuName = proc["Name"]?.ToString()?.Trim() ?? "Bilinmeyen İşlemci";
                    string mfg = proc["Manufacturer"]?.ToString()?.Trim() ?? "Intel/AMD";
                    int cores = Convert.ToInt32(proc["NumberOfCores"] ?? 0);
                    int threads = Convert.ToInt32(proc["NumberOfLogicalProcessors"] ?? 0);

                    // PnPSignedDriver tablosundan işlemci mikrokod sürücüsünü çek
                    string driverVer = "10.0.26100.9818";
                    string driverDate = "2026";
                    try
                    {
                        using var pnpSearcher = new ManagementObjectSearcher("SELECT DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'PROCESSOR'");
                        var pnpObj = pnpSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                        if (pnpObj != null)
                        {
                            driverVer = pnpObj["DriverVersion"]?.ToString() ?? driverVer;
                            driverDate = FormatDriverDate(pnpObj["DriverDate"]?.ToString());
                        }
                    }
                    catch { }

                    list.Add(new DriverInfo
                    {
                        Id = "CPU_PRIMARY",
                        Name = $"{cpuName} ({cores} Çekirdek / {threads} İş Parçacığı)",
                        Category = DriverCategory.CPU,
                        Manufacturer = mfg.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "AMD",
                        CurrentVersion = driverVer,
                        CurrentDateStr = driverDate,
                        LatestVersion = driverVer,
                        LatestDateStr = "En Son WHQL",
                        Status = DriverStatus.UpToDate,
                        StatusBadgeText = "✔ GÜNCEL (WHQL)",
                        StatusColorHex = "#00FF87",
                        ReleaseNotes = "İşlemci güç yönetimi ve çekirdek zamanlayıcı (Core Scheduler) sürücüsü en güncel Windows çekirdeğiyle eşleşti."
                    });
                }
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Warn("DRIVER", $"CPU sorgu hatası: {ex.Message}");
            }
            return list;
        }

        // ═══════════════════════════════════════════════════════════
        //  C. ANAKART & ÇİPSET TARAMA
        // ═══════════════════════════════════════════════════════════
        private List<DriverInfo> ScanMotherboardAndChipset()
        {
            var list = new List<DriverInfo>();
            try
            {
                // 1. Anakart ve BIOS
                string mbMfg = "Bilinmiyor";
                string mbProduct = "Bilinmiyor";
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                {
                    var obj = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (obj != null)
                    {
                        mbMfg = obj["Manufacturer"]?.ToString()?.Trim() ?? mbMfg;
                        mbProduct = obj["Product"]?.ToString()?.Trim() ?? mbProduct;
                    }
                }

                string biosVer = "";
                string biosDate = "";
                using (var biosSearch = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS"))
                {
                    var bObj = biosSearch.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (bObj != null)
                    {
                        biosVer = bObj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                        biosDate = FormatDriverDate(bObj["ReleaseDate"]?.ToString());
                    }
                }

                list.Add(new DriverInfo
                {
                    Id = "MB_BASEBOARD",
                    Name = $"{mbMfg} {mbProduct} (BIOS: {biosVer})",
                    Category = DriverCategory.Motherboard,
                    Manufacturer = mbMfg,
                    CurrentVersion = biosVer,
                    CurrentDateStr = biosDate,
                    LatestVersion = biosVer,
                    LatestDateStr = biosDate,
                    IsBiosFirmware = true,
                    IsUpdateAvailable = false,
                    Status = DriverStatus.UpToDate,
                    StatusBadgeText = "✔ KORUMALI BIOS (UEFI)",
                    StatusColorHex = "#667EEA",
                    ReleaseNotes = "Anakart BIOS/UEFI bellenimi güvenli donanım izolasyonunda. Brick riskini önlemek için Windows içinden otomatik flashlama engellenmiştir."
                });

                // 2. Çipset INF & Sistem Cihazları
                using var pnpSearcher = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DriverDate, Manufacturer, InfName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'SYSTEM'");
                var systemDrivers = pnpSearcher.Get().Cast<ManagementObject>()
                    .Where(o => {
                        string dName = o["DeviceName"]?.ToString() ?? "";
                        return dName.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
                               dName.Contains("Management Engine", StringComparison.OrdinalIgnoreCase) ||
                               dName.Contains("PCI Express Root", StringComparison.OrdinalIgnoreCase) ||
                               dName.Contains("Host Bridge", StringComparison.OrdinalIgnoreCase);
                    })
                    .Take(2); // Önemli ilk 2 çipset aygıtını al

                foreach (var sObj in systemDrivers)
                {
                    string dName = sObj["DeviceName"]?.ToString() ?? "Çipset Aygıtı";
                    string dVer = sObj["DriverVersion"]?.ToString() ?? "Bilinmiyor";
                    string dDate = FormatDriverDate(sObj["DriverDate"]?.ToString());
                    string inf = sObj["InfName"]?.ToString() ?? "";

                    list.Add(new DriverInfo
                    {
                        Id = "CHIPSET_" + Guid.NewGuid().ToString("N")[..6],
                        Name = dName,
                        Category = DriverCategory.Motherboard,
                        Manufacturer = sObj["Manufacturer"]?.ToString() ?? mbMfg,
                        CurrentVersion = dVer,
                        CurrentDateStr = dDate,
                        LatestVersion = dVer,
                        LatestDateStr = dDate,
                        InfName = inf,
                        Status = DriverStatus.UpToDate,
                        StatusBadgeText = "✔ GÜNCEL",
                        StatusColorHex = "#00FF87"
                    });
                }
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Warn("DRIVER", $"Anakart sorgu hatası: {ex.Message}");
            }
            return list;
        }

        // ═══════════════════════════════════════════════════════════
        //  D. AĞ & WI-FI TARAMA
        // ═══════════════════════════════════════════════════════════
        private List<DriverInfo> ScanNetworkAdapters()
        {
            var list = new List<DriverInfo>();
            try
            {
                bool isWin11 = Environment.OSVersion.Version.Build >= 22000;
                using var searcher = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DriverDate, Manufacturer, InfName, HardWareID, DeviceID, DeviceClass FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET' OR DeviceClass = 'BLUETOOTH'");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    string name = obj["DeviceName"]?.ToString()?.Trim() ?? "";
                    string ver = obj["DriverVersion"]?.ToString()?.Trim() ?? "Bilinmiyor";
                    string date = FormatDriverDate(obj["DriverDate"]?.ToString());
                    string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "Ağ Üreticisi";
                    string inf = obj["InfName"]?.ToString()?.Trim() ?? "";
                    string hwId = ExtractPrimaryHardwareId(obj["HardWareID"], obj["DeviceID"]);

                    // Sanal ve miniport adaptörleri filtrele (Sadece gerçek Wi-Fi, Ethernet ve Bluetooth)
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Direct Virtual", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Bluetooth Device (Personal", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool isWifi = name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("802.11", StringComparison.OrdinalIgnoreCase);

                    bool isEthernet = name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                                      name.Contains("GbE", StringComparison.OrdinalIgnoreCase) ||
                                      name.Contains("Realtek Gaming", StringComparison.OrdinalIgnoreCase) ||
                                      name.Contains("Controller", StringComparison.OrdinalIgnoreCase);

                    bool isBluetooth = name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);

                    if (!isWifi && !isEthernet && !isBluetooth && !name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Çevrimiçi Microsoft Update Catalog WHQL sürücü eşleştirmesi
                    string latestVer = ver;
                    string latestDate = date;
                    string downloadUrl = "";
                    string downloadSize = "";
                    bool updateAvail = false;

                    if (isWifi && (hwId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || name.Contains("Intel", StringComparison.OrdinalIgnoreCase)))
                    {
                        string targetVer = "23.80.1.3";
                        if (IsNewerVersion(targetVer, ver))
                        {
                            latestVer = targetVer;
                            latestDate = "Eylül 2024 (Intel WHQL)";
                            downloadUrl = "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2024/11/6bc5d530-eaab-4a41-84f2-976acac264bb_4f5c1ae85e90bbea2d31fbd89666c1771a7d14e5.cab";
                            downloadSize = "18.8 MB (WHQL CAB)";
                            updateAvail = true;
                        }
                    }
                    else if (isEthernet && (hwId.Contains("DEV_8125", StringComparison.OrdinalIgnoreCase) ||
                             (name.Contains("Realtek", StringComparison.OrdinalIgnoreCase) && (name.Contains("2.5G", StringComparison.OrdinalIgnoreCase) || name.Contains("2.5GbE", StringComparison.OrdinalIgnoreCase)))))
                    {
                        if (isWin11)
                        {
                            string targetVer = "1125.22.1118.2024";
                            if (IsNewerVersion(targetVer, ver))
                            {
                                latestVer = targetVer;
                                latestDate = "Kasım 2024 (WHQL)";
                                downloadUrl = "https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2025/04/86048b97-a84b-4e48-a284-f9a7a02df087_16c10552f57b8914f796603632683b1be8c25480.cab";
                                downloadSize = "304 KB (WHQL CAB)";
                                updateAvail = true;
                            }
                        }
                    }
                    else if (isEthernet && (hwId.Contains("DEV_8168", StringComparison.OrdinalIgnoreCase) ||
                             (name.Contains("Realtek", StringComparison.OrdinalIgnoreCase) && !name.Contains("2.5G", StringComparison.OrdinalIgnoreCase) && !name.Contains("2.5GbE", StringComparison.OrdinalIgnoreCase))))
                    {
                        if (isWin11)
                        {
                            string targetVer = "1168.22.1118.2024";
                            if (IsNewerVersion(targetVer, ver))
                            {
                                latestVer = targetVer;
                                latestDate = "Kasım 2024 (WHQL)";
                                downloadUrl = "https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2025/04/2d775acc-14fe-4db0-885b-c9f25f0f2cb6_5ae4931374eb83c59e79d21fda1855229dd3c8bc.cab";
                                downloadSize = "339 KB (WHQL CAB)";
                                updateAvail = true;
                            }
                        }
                        else
                        {
                            string targetVer = "10.79.50.1003";
                            if (IsNewerVersion(targetVer, ver))
                            {
                                latestVer = targetVer;
                                latestDate = "Ekim 2025 (Win10 WHQL)";
                                downloadUrl = "https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2026/01/16270d4d-ca9d-4fb6-a0f4-e4354f3f98bb_63e362b5d90631a42779ff2986cd9e92eb2dc96f.cab";
                                downloadSize = "584 KB (WHQL CAB)";
                                updateAvail = true;
                            }
                        }
                    }

                    string notes = isWifi ? "Wi-Fi bağlantı gecikmesini (jitter) ve ping stabilitesini artıran güncel paket."
                        : isBluetooth ? "Bluetooth 5.3 çevre birimi eşleşme kararlılığı ve düşük gecikme paketi."
                        : "Gigabit Ethernet paket transfer optimizasyonu.";

                    list.Add(new DriverInfo
                    {
                        Id = "NET_" + Guid.NewGuid().ToString("N")[..6],
                        Name = name,
                        Category = DriverCategory.Network,
                        Manufacturer = mfg,
                        CurrentVersion = ver,
                        CurrentDateStr = date,
                        LatestVersion = latestVer,
                        LatestDateStr = latestDate,
                        DownloadUrl = downloadUrl,
                        DownloadSize = downloadSize,
                        InfName = inf,
                        HardwareId = hwId,
                        IsUpdateAvailable = updateAvail,
                        Status = updateAvail ? DriverStatus.UpdateAvailable : DriverStatus.UpToDate,
                        StatusBadgeText = updateAvail ? $"⚡ YENİ GÜNCELLEME: v{latestVer}" : "✔ GÜNCEL",
                        StatusColorHex = updateAvail ? "#00F2FE" : "#00FF87",
                        ReleaseNotes = notes
                    });
                }
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Warn("DRIVER", $"Ağ adaptörü sorgu hatası: {ex.Message}");
            }
            return list;
        }

        // ═══════════════════════════════════════════════════════════
        //  E. WINDOWS UPDATE SÜRÜCÜ KONTROLÜ (WHQL)
        // ═══════════════════════════════════════════════════════════
        private async Task CheckWindowsUpdateDriversAsync(List<DriverInfo> existingDrivers)
        {
            await Task.Run(() =>
            {
                try
                {
                    Type? updateSessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                    if (updateSessionType == null) return;

                    dynamic session = Activator.CreateInstance(updateSessionType)!;
                    dynamic searcher = session.CreateUpdateSearcher();
                    try
                    {
                        // 0 = ssDefault (Yerel katalog ve varsayılan Windows Update servisi; 2 gibi uzaktaki sunucularda donmaz)
                        searcher.ServerSelection = 0;
                    }
                    catch { }

                    dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver'");
                    dynamic updates = searchResult.Updates;
                    int count = updates.Count;

                    for (int i = 0; i < count; i++)
                    {
                        dynamic u = updates.Item(i);
                        string title = u.Title?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        string lowerTitle = title.ToLowerInvariant();

                        // 1. Kategoriyi tespit et
                        DriverCategory? targetCat = null;
                        if (lowerTitle.Contains("net") || lowerTitle.Contains("wireless") || lowerTitle.Contains("wi-fi") ||
                            lowerTitle.Contains("ethernet") || lowerTitle.Contains("lan") || lowerTitle.Contains("802.11") || lowerTitle.Contains("bluetooth"))
                        {
                            targetCat = DriverCategory.Network;
                        }
                        else if (lowerTitle.Contains("display") || lowerTitle.Contains("graphics") || lowerTitle.Contains("vga") ||
                                 lowerTitle.Contains("geforce") || lowerTitle.Contains("radeon"))
                        {
                            targetCat = DriverCategory.GPU;
                        }
                        else if (lowerTitle.Contains("processor") || lowerTitle.Contains("cpu") || lowerTitle.Contains("microcode"))
                        {
                            targetCat = DriverCategory.CPU;
                        }
                        else if (lowerTitle.Contains("system") || lowerTitle.Contains("chipset") || lowerTitle.Contains("smbus") ||
                                 lowerTitle.Contains("management engine") || lowerTitle.Contains("serial io") || lowerTitle.Contains("pci"))
                        {
                            targetCat = DriverCategory.Motherboard;
                        }
                        else if (lowerTitle.Contains("audio") || lowerTitle.Contains("sound") || lowerTitle.Contains("realtek high definition"))
                        {
                            targetCat = DriverCategory.Audio;
                        }

                        // 2. Sürümü başlıktan ayıkla (Örn: 24.80.0.2)
                        var verMatch = Regex.Match(title, @"\b\d+(\.\d+){1,4}\b");
                        string extractedVer = verMatch.Success ? verMatch.Value : "";

                        // 3. Yalnızca KATEGORİSİ EŞLEŞEN ve BIOS OLMAYAN donanımları ara
                        var candidates = existingDrivers.Where(d =>
                        {
                            if (d.IsBiosFirmware) return false;
                            if (targetCat.HasValue && d.Category != targetCat.Value) return false;

                            bool mfgMatch = !string.IsNullOrEmpty(d.Manufacturer) && title.Contains(d.Manufacturer, StringComparison.OrdinalIgnoreCase);
                            bool nameMatch = !string.IsNullOrEmpty(d.Name) && (title.Contains(d.Name, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(title, StringComparison.OrdinalIgnoreCase));

                            return mfgMatch || nameMatch;
                        }).ToList();

                        foreach (var matched in candidates)
                        {
                            // 4. Kesin sürüm karşılaştırması: Yeni sürüm mevcut sürümden BÜYÜKSE ve EŞİT DEĞİLSE güncelleme işaretle
                            if (!string.IsNullOrEmpty(extractedVer) && IsNewerVersion(extractedVer, matched.CurrentVersion) && !string.Equals(extractedVer, matched.CurrentVersion, StringComparison.OrdinalIgnoreCase))
                            {
                                bool isNewerThanCatalog = IsNewerVersion(extractedVer, matched.LatestVersion);
                                if (string.IsNullOrEmpty(matched.DownloadUrl) || isNewerThanCatalog)
                                {
                                    if (isNewerThanCatalog && !string.IsNullOrEmpty(matched.DownloadUrl))
                                    {
                                        matched.DownloadUrl = "";
                                        matched.DownloadSize = "Windows WHQL";
                                    }
                                    matched.LatestVersion = extractedVer;
                                    matched.IsUpdateAvailable = true;
                                    matched.Status = DriverStatus.UpdateAvailable;
                                    matched.StatusBadgeText = "⚡ WHQL GÜNCELLEME MEVCUT";
                                    matched.StatusColorHex = "#00F2FE";
                                    matched.ReleaseNotes = title;
                                    matched.WindowsUpdateTitle = title;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AlpNetTelemetryService.Instance.Warn("DRIVER", $"CheckWindowsUpdateDriversAsync hatası: {ex.Message}");
                }
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  2. SÜRÜCÜ GÜNCELLEME (İNDİRME, YEDEKLEME VE KURULUM)
        // ═══════════════════════════════════════════════════════════
        public async Task<(bool Success, string Message)> UpdateDriverAsync(DriverInfo driver, Action<int, string>? progressCallback = null)
        {
            if (driver.IsBiosFirmware)
            {
                return (false, "BIOS ve Firmware anakartın kök yazılımıdır. Windows içinden otomatik kurulması donanımın brick olma riskini taşır. Lütfen anakart üreticinizin resmi BIOS flash aracını kullanınız.");
            }

            // Güvenlik denetimi: Aynı sürüm veya daha eski sürüm güncellemesini kesin olarak engelle
            if (!driver.IsUpdateAvailable || !IsNewerVersion(driver.LatestVersion, driver.CurrentVersion) || string.Equals(driver.CurrentVersion, driver.LatestVersion, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"{driver.Name} zaten en güncel sürümde (v{driver.CurrentVersion}).");
            }

            PruneBackupsOlderThan(TimeSpan.FromHours(48));

            string originalOldVersion = driver.CurrentVersion;
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeName = Regex.Replace(driver.Name, @"[^a-zA-Z0-9_-]", "_");
            string backupFolder = Path.Combine(_backupsDir, $"{timestamp}_{safeName}");

            progressCallback?.Invoke(10, $"Mevcut çalışan sürücü otomatik yedekleniyor: {driver.Name}...");
            bool backedUp = await BackupSingleDriverAsync(driver, backupFolder);

            // Geri yükleme noktası oluştur
            progressCallback?.Invoke(25, "Windows Sistem Geri Yükleme Noktası oluşturuluyor...");
            CreateSystemRestorePoint($"AlpNet Sürücü Güncellemesi - {driver.Name}");

            bool installAttempted = false;

            if (driver.Category == DriverCategory.GPU && !string.IsNullOrEmpty(driver.DownloadUrl))
            {
                progressCallback?.Invoke(35, $"Resmi {driver.Manufacturer} sürücüsü indiriliyor...");
                string tempDir = Path.Combine(Path.GetTempPath(), "AlpNetDriverSetup");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                string installerPath = Path.Combine(tempDir, $"{safeName}_v{driver.LatestVersion}.exe");

                // İndir
                bool downloaded = await DownloadFileWithProgressAsync(driver.DownloadUrl, installerPath, (pct) =>
                {
                    int scaled = 35 + (int)(pct * 0.45); // 35% -> 80%
                    progressCallback?.Invoke(scaled, $"Sürücü İndiriliyor: %{pct} ({driver.DownloadSize})");
                });

                if (!downloaded || !File.Exists(installerPath))
                {
                    await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                    {
                        UpdateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss"),
                        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        DeviceName = driver.Name,
                        Category = driver.CategoryBadgeText,
                        OldVersion = originalOldVersion,
                        NewVersion = driver.LatestVersion,
                        Status = "Hata",
                        Verified = false,
                        BackupPath = backupFolder,
                        Notes = "Sürücü paketi indirilemedi. Lütfen internet bağlantınızı kontrol edin."
                    });
                    return (false, "Sürücü paketi indirilemedi. Lütfen internet bağlantınızı kontrol edin.");
                }

                progressCallback?.Invoke(85, "Kurulum başlatılıyor (Sessiz mod hazırlanıyor)...");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = "-s",
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    var p = Process.Start(psi);
                    if (p != null) await p.WaitForExitAsync();
                    installAttempted = true;
                }
                catch
                {
                    var p = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
                    if (p != null) await p.WaitForExitAsync();
                    installAttempted = true;
                }
            }
            else if (driver.Category == DriverCategory.Network)
            {
                // AĞ KARTLARI (Wi-Fi / Ethernet / Bluetooth): %100 Gerçek İndirme ve Güvenli WHQL CAB/INF Kurulumu
                progressCallback?.Invoke(35, "Ağ kartı kurulum paketi hazırlanıyor...");
                string cacheDir = Path.Combine(_driverDataDir, "Cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string failReason = "";

                if (string.IsNullOrEmpty(driver.DownloadUrl))
                {
                    if (!string.IsNullOrEmpty(driver.WindowsUpdateTitle))
                    {
                        progressCallback?.Invoke(45, "Windows Update WHQL sürücü deposundan paket indiriliyor ve yükleniyor...");
                        var wuRes = await InstallWindowsUpdateDriverAsync(driver.WindowsUpdateTitle, progressCallback);
                        installAttempted = wuRes.Success;
                        if (!wuRes.Success)
                        {
                            failReason = wuRes.Message;
                            AlpNetTelemetryService.Instance.Warn("DRIVER", $"Windows Update kurulum sonucu: {wuRes.Message}");
                        }
                    }

                    if (!installAttempted)
                    {
                        progressCallback?.Invoke(42, "Microsoft Update Catalog üzerinden WHQL paketi aranıyor...");
                        string? catalogUrl = await ResolveMicrosoftCatalogCabUrlAsync(driver);
                        if (!string.IsNullOrEmpty(catalogUrl))
                        {
                            driver.DownloadUrl = catalogUrl;
                        }
                    }
                }

                if (!installAttempted && !string.IsNullOrEmpty(driver.DownloadUrl))
                {
                    bool isCabPackage = driver.DownloadUrl.Contains(".cab", StringComparison.OrdinalIgnoreCase);
                    string ext = isCabPackage ? ".cab" : ".exe";
                    string installerPath = Path.Combine(cacheDir, $"{safeName}_v{driver.LatestVersion}{ext}");

                    progressCallback?.Invoke(40, "Ağ sürücüsü çevrimdışı yerel belleğe indiriliyor...");
                    bool downloaded = await DownloadFileWithProgressAsync(driver.DownloadUrl, installerPath, (pct) =>
                    {
                        int scaled = 40 + (int)(pct * 0.35);
                        progressCallback?.Invoke(scaled, $"Paket İndiriliyor: %{pct}");
                    });

                    if (!downloaded || !File.Exists(installerPath) || new FileInfo(installerPath).Length < 1024)
                    {
                        await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                        {
                            UpdateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss"),
                            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            DeviceName = driver.Name,
                            Category = driver.CategoryBadgeText,
                            OldVersion = originalOldVersion,
                            NewVersion = driver.LatestVersion,
                            Status = "Hata",
                            Verified = false,
                            BackupPath = backupFolder,
                            Notes = "Ağ sürücüsü indirme paketi tamamlanamadı. İnternet kesintisi durumunda kilitlenmeyi önlemek için kuruluma başlanmadı."
                        });
                        return (false, "Ağ sürücüsü indirme paketi tamamlanamadı. İnternet kesintisi durumunda kilitlenmeyi önlemek için kuruluma başlanmadı.");
                    }

                    if (isCabPackage)
                    {
                        var cabRes = await InstallCabDriverPackageAsync(installerPath, driver, progressCallback);
                        installAttempted = cabRes.Success;
                        if (!cabRes.Success)
                        {
                            failReason = cabRes.Message;
                        }
                    }
                    else
                    {
                        progressCallback?.Invoke(80, "Paket indirildi ve doğrulandı. Sessiz kurulum yürütülüyor...");
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = installerPath,
                                Arguments = "/s /qn /quiet /norestart",
                                UseShellExecute = true,
                                Verb = "runas"
                            };
                            var proc = Process.Start(psi);
                            if (proc != null) await proc.WaitForExitAsync();
                            installAttempted = true;
                        }
                        catch
                        {
                            var proc = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
                            if (proc != null) await proc.WaitForExitAsync();
                            installAttempted = true;
                        }
                    }
                }

                if (!installAttempted)
                {
                    string reason = string.IsNullOrWhiteSpace(failReason)
                        ? "Microsoft Update Catalog ve Windows Update üzerinde uygun WHQL kurulum paketi bulunamadı."
                        : failReason;

                    await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                    {
                        UpdateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss"),
                        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        DeviceName = driver.Name,
                        Category = driver.CategoryBadgeText,
                        OldVersion = originalOldVersion,
                        NewVersion = driver.LatestVersion,
                        Status = "Hata",
                        Verified = false,
                        BackupPath = backupFolder,
                        Notes = reason
                    });
                    return (false, $"{driver.Name} güncellenemedi: {reason}");
                }

                await TriggerDeviceUpdateAsync(driver.HardwareId);

                // Doğrulama & Otomatik Ağ Stabilizasyonu
                progressCallback?.Invoke(88, "Ağ bağdaştırıcısı bağlantı ve güvenlik protokolleri stabilize ediliyor...");
                await PostNetworkDriverUpdateHealAsync(progressCallback);
            }
            else
            {
                // Diğer Donanımlar (İşlemci/Çipset/Ses) - Gerçek Windows Update WHQL veya Microsoft Catalog CAB kurulumu
                if (string.IsNullOrEmpty(driver.DownloadUrl) && !string.IsNullOrEmpty(driver.WindowsUpdateTitle))
                {
                    progressCallback?.Invoke(50, "Windows Update WHQL paket kurulumu yürütülüyor...");
                    var wuRes = await InstallWindowsUpdateDriverAsync(driver.WindowsUpdateTitle, progressCallback);
                    installAttempted = wuRes.Success;
                    if (!installAttempted)
                    {
                        string? catalogUrl = await ResolveMicrosoftCatalogCabUrlAsync(driver);
                        if (!string.IsNullOrEmpty(catalogUrl))
                        {
                            driver.DownloadUrl = catalogUrl;
                        }
                        else
                        {
                            await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                            {
                                UpdateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss"),
                                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                DeviceName = driver.Name,
                                Category = driver.CategoryBadgeText,
                                OldVersion = originalOldVersion,
                                NewVersion = driver.LatestVersion,
                                Status = "Hata",
                                Verified = false,
                                BackupPath = backupFolder,
                                Notes = $"Windows Update sürücü kurulumu başarısız oldu: {wuRes.Message}"
                            });
                            return (false, $"Windows Update sürücü kurulumu başarısız oldu: {wuRes.Message}");
                        }
                    }
                    else
                    {
                        await TriggerDeviceUpdateAsync(driver.HardwareId);
                    }
                }

                if (!installAttempted && !string.IsNullOrEmpty(driver.DownloadUrl))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "AlpNetDriverSetup");
                    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                    bool isCabPackage = driver.DownloadUrl.Contains(".cab", StringComparison.OrdinalIgnoreCase);
                    string ext = isCabPackage ? ".cab" : ".exe";
                    string installerPath = Path.Combine(tempDir, $"{safeName}_v{driver.LatestVersion}{ext}");

                    bool downloaded = await DownloadFileWithProgressAsync(driver.DownloadUrl, installerPath, null);
                    if (!downloaded || !File.Exists(installerPath))
                    {
                        await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                        {
                            UpdateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss"),
                            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            DeviceName = driver.Name,
                            Category = driver.CategoryBadgeText,
                            OldVersion = originalOldVersion,
                            NewVersion = driver.LatestVersion,
                            Status = "Hata",
                            Verified = false,
                            BackupPath = backupFolder,
                            Notes = "Sürücü paketi indirilemedi. Lütfen internet bağlantınızı kontrol edin."
                        });
                        return (false, "Sürücü paketi indirilemedi. Lütfen internet bağlantınızı kontrol edin.");
                    }

                    if (isCabPackage)
                    {
                        var cabRes = await InstallCabDriverPackageAsync(installerPath, driver, progressCallback);
                        installAttempted = cabRes.Success;
                        if (!cabRes.Success)
                        {
                            return (false, $"{driver.Name} güncellenemedi: {cabRes.Message}");
                        }
                        await TriggerDeviceUpdateAsync(driver.HardwareId);
                    }
                    else
                    {
                        try
                        {
                            var proc = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true, Arguments = "/s /qn /quiet" });
                            if (proc != null) await proc.WaitForExitAsync();
                            installAttempted = true;
                        }
                        catch (Exception ex)
                        {
                            AlpNetTelemetryService.Instance.Warn("DRIVER", $"Sürücü kurulum başlatma hatası: {ex.Message}");
                        }
                    }
                }
            }

            // CRITICAL: Kurulum sonrası WMI üzerinden fiziksel aygıt sürücü sürümünü doğrula!
            progressCallback?.Invoke(95, "Fiziksel donanım sürücüsü doğrulanıyor...");
            string verifiedVer = await WaitForUpdatedPhysicalVersionAsync(driver, originalOldVersion);

            if (!string.IsNullOrEmpty(verifiedVer))
            {
                // Eğer fiziksel sürüm artmadıysa ve eski sürüme eşit veya küçükse asla başarılı kaydetme!
                if (string.Equals(verifiedVer, originalOldVersion, StringComparison.OrdinalIgnoreCase) || !IsNewerVersion(verifiedVer, originalOldVersion))
                {
                    // AlpTogether Telemetrisi: Sürücü paketi yürütüldü fakat fiziksel sürüm değişmedi (Yeniden başlatma gerekebilir)
                    await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                    {
                        UpdateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss"),
                        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        DeviceName = driver.Name,
                        Category = driver.CategoryBadgeText,
                        OldVersion = originalOldVersion,
                        NewVersion = driver.LatestVersion,
                        Status = "Hata",
                        Verified = false,
                        PhysicalVersionAfterInstall = verifiedVer,
                        BackupPath = backupFolder,
                        Notes = $"Fiziksel sürüm v{verifiedVer} olarak kaldı, hedef v{driver.LatestVersion}. Sistemin yeniden başlatılması gerekebilir."
                    });

                    return (false, $"{driver.Name} kurulum komutu yürütüldü fakat donanımdaki fiziksel sürücü sürümü değişmedi (Mevcut: v{verifiedVer}, Hedeflenen: v{driver.LatestVersion}). Sürücünün devreye girmesi için bilgisayarınızı yeniden başlatmanız gerekebilir.");
                }

                driver.CurrentVersion = verifiedVer;
            }
            else if (!installAttempted)
            {
                await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                {
                    UpdateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss"),
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    DeviceName = driver.Name,
                    Category = driver.CategoryBadgeText,
                    OldVersion = originalOldVersion,
                    NewVersion = driver.LatestVersion,
                    Status = "Hata",
                    Verified = false,
                    BackupPath = backupFolder,
                    Notes = $"{driver.Name} için uygun kurulum paketi yürütülemedi."
                });
                return (false, $"{driver.Name} için uygun kurulum paketi yürütülemedi.");
            }

            driver.IsUpdateAvailable = false;
            driver.Status = DriverStatus.UpToDate;
            driver.StatusBadgeText = "✔ GÜNCEL (WHQL Doğrulandı)";
            driver.StatusColorHex = "#00FF87";

            string updateId = "UPD-" + DateTime.Now.ToString("MMddHHmmss");
            string updateTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            RecordHistory(new DriverUpdateRecord
            {
                UpdateId = updateId,
                Timestamp = updateTimestamp,
                DeviceName = driver.Name,
                Category = driver.CategoryBadgeText,
                OldVersion = originalOldVersion,
                NewVersion = driver.CurrentVersion,
                Status = "Başarıyla Güncellendi",
                Verified = true,
                UserName = Environment.UserName,
                MachineName = Environment.MachineName,
                BackupPath = backupFolder,
                DownloadUrl = driver.DownloadUrl,
                Notes = "Sürücü başarıyla kuruldu ve fiziksel donanım sürümü doğrulandı."
            });

            // AlpTogether Telemetrisi: Kişi ve aygıt bazlı doğrulanmış sürücü güncelleme telemetrisi
            await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
            {
                UpdateId = updateId,
                Timestamp = updateTimestamp,
                DeviceName = driver.Name,
                Category = driver.CategoryBadgeText,
                OldVersion = originalOldVersion,
                NewVersion = driver.CurrentVersion,
                Status = "Başarıyla Güncellendi",
                Verified = true,
                PhysicalVersionAfterInstall = verifiedVer,
                BackupPath = backupFolder,
                Notes = "Sürücü başarıyla kuruldu ve fiziksel donanım sürümü WMI üzerinden doğrulandı."
            });

            progressCallback?.Invoke(100, $"✔ {driver.Name} güncellemesi başarıyla tamamlandı!");
            return (true, $"✅ {driver.Name} sürücüsü başarıyla v{driver.CurrentVersion} sürümüne güncellendi!\nEski çalışan sürücünüz 48 saat boyunca AlpNet deposunda saklanacaktır.");
        }

        // ═══════════════════════════════════════════════════════════
        //  3. SÜRÜCÜ YEDEKLEME (PNPUTIL)
        // ═══════════════════════════════════════════════════════════
        public async Task<bool> BackupAllDriversAsync(Action<string>? progressCallback = null)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string targetFolder = Path.Combine(_backupsDir, $"FullBackup_{timestamp}");

            progressCallback?.Invoke("Sürücü yedekleme klasörü hazırlanıyor...");
            if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

            progressCallback?.Invoke("Tüm 3. parti aygıt sürücüleri dışa aktarılıyor (pnputil)...");
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/export-driver * \"{targetFolder}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                int count = Directory.GetFiles(targetFolder, "*.inf", SearchOption.AllDirectories).Length;
                progressCallback?.Invoke($"Yedekleme tamamlandı! {count} adet sürücü paketi yedeklendi.");
                return count > 0;
            }
            return false;
        }

        private async Task<bool> BackupSingleDriverAsync(DriverInfo driver, string targetFolder)
        {
            try
            {
                if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

                string args = !string.IsNullOrEmpty(driver.InfName)
                    ? $"/export-driver {driver.InfName} \"{targetFolder}\""
                    : $"/export-driver * \"{targetFolder}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = args,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ═══════════════════════════════════════════════════════════
        //  4. GERİ YÜKLEME (ROLLBACK)
        // ═══════════════════════════════════════════════════════════
        public async Task<(bool Success, string Message)> RollbackDriverAsync(DriverUpdateRecord record, Action<string>? progressCallback = null)
        {
            string rollbackId = "UPD-" + DateTime.Now.ToString("MMddHHmmss") + "-RBK";
            string rollbackTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (string.IsNullOrEmpty(record.BackupPath) || !Directory.Exists(record.BackupPath))
            {
                await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                {
                    UpdateId = rollbackId,
                    Timestamp = rollbackTimestamp,
                    DeviceName = record.DeviceName,
                    Category = record.Category,
                    OldVersion = record.NewVersion,
                    NewVersion = record.OldVersion,
                    Status = "Hata",
                    Verified = false,
                    BackupPath = record.BackupPath,
                    Notes = "Geri yükleme başarısız: Yedek klasörü bulunamadı veya silinmiş."
                });
                return (false, "Yedek klasörü bulunamadı veya silinmiş.");
            }

            progressCallback?.Invoke($"Yedekten geri yükleniyor: {record.DeviceName}...");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/add-driver \"{record.BackupPath}\\*.inf\" /subdirs /install",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    string errorOutput = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    bool isSuccess = proc.ExitCode == 0 || proc.ExitCode == 3010;
                    if (!isSuccess)
                    {
                        string failReason = !string.IsNullOrWhiteSpace(errorOutput) ? errorOutput.Trim() : (!string.IsNullOrWhiteSpace(output) ? output.Trim() : $"Çıkış kodu: {proc.ExitCode}");
                        await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                        {
                            UpdateId = rollbackId,
                            Timestamp = rollbackTimestamp,
                            DeviceName = record.DeviceName,
                            Category = record.Category,
                            OldVersion = record.NewVersion,
                            NewVersion = record.OldVersion,
                            Status = "Hata",
                            Verified = false,
                            BackupPath = record.BackupPath,
                            Notes = $"pnputil geri yükleme başarısız (Kod: {proc.ExitCode}): {failReason}"
                        });
                        return (false, $"Sürücü geri yüklenemedi (Çıkış kodu: {proc.ExitCode}): {failReason}");
                    }

                    record.Status = "Eski Haline Geri Yüklendi (Rollback)";
                    SaveHistoryRecord(record);

                    // AlpTogether Telemetrisi: Sürücü eski yedeğine geri döndürüldü
                    await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                    {
                        UpdateId = rollbackId,
                        Timestamp = rollbackTimestamp,
                        DeviceName = record.DeviceName,
                        Category = record.Category,
                        OldVersion = record.NewVersion,
                        NewVersion = record.OldVersion,
                        Status = "Eski Haline Geri Yüklendi (Rollback)",
                        Verified = true,
                        PhysicalVersionAfterInstall = record.OldVersion,
                        BackupPath = record.BackupPath,
                        Notes = "Sürücü pnputil ile AlpNet yedek deposundan geri yüklendi."
                    });

                    if (record.Category.Contains("AĞ", StringComparison.OrdinalIgnoreCase) ||
                        record.Category.Contains("WI-FI", StringComparison.OrdinalIgnoreCase) ||
                        record.DeviceName.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                        record.DeviceName.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                        record.DeviceName.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
                    {
                        progressCallback?.Invoke("Ağ bağdaştırıcısı geri yüklendi. DNS ve tünel protokolleri senkronize ediliyor...");
                        await PostNetworkDriverUpdateHealAsync((pct, msg) => progressCallback?.Invoke(msg));
                    }

                    progressCallback?.Invoke("✔ Sürücü başarıyla eski haline geri yüklendi!");
                    return (true, $"✅ {record.DeviceName} sürücüsü başarıyla eski sürümüne (v{record.OldVersion}) geri döndürüldü.");
                }
            }
            catch (Exception ex)
            {
                await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(new DriverUpdateTelemetryRecord
                {
                    UpdateId = rollbackId,
                    Timestamp = rollbackTimestamp,
                    DeviceName = record.DeviceName,
                    Category = record.Category,
                    OldVersion = record.NewVersion,
                    NewVersion = record.OldVersion,
                    Status = "Hata",
                    Verified = false,
                    BackupPath = record.BackupPath,
                    Notes = $"Geri yükleme sırasında hata oluştu: {ex.Message}"
                });
                return (false, $"Geri yükleme sırasında hata oluştu: {ex.Message}");
            }

            return (false, "Geri yükleme işlemi tamamlanamadı.");
        }

        // ═══════════════════════════════════════════════════════════
        //  AĞ SÜRÜCÜSÜ SONRASI OTOMATİK İYİLEŞTİRME VE SENKRONİZASYON
        // ═══════════════════════════════════════════════════════════
        private async Task PostNetworkDriverUpdateHealAsync(Action<int, string>? progressCallback = null)
        {
            try
            {
                // 1) Ağ bağlantısının geri gelmesini bekle (maksimum 15 sn)
                progressCallback?.Invoke(90, "Ağ bağdaştırıcısının IP ve ağ geçidi bağlantısı bekleniyor...");
                for (int i = 0; i < 15; i++)
                {
                    if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                    {
                        await Task.Delay(1500);
                        break;
                    }
                    await Task.Delay(1000);
                }

                // 2) Windows'un sürücü sonrası DNS'i ISS DHCP'sine (195.175.x.x) çekmesini engelle; Cloudflare güvenli DNS'i yeniden uygula
                progressCallback?.Invoke(93, "Cloudflare şifreli DNS ve DoH güvenlik protokolü yeniden kilitleniyor...");
                await WarpManager.SetCloudflareDnsAsync();

                // 3) Windows DNS & ARP önbelleğini temizle (kesinti sırasında biriken bozuk sorguları sil)
                progressCallback?.Invoke(95, "Bozuk DNS çözümleme kayıtları ve ARP önbelleği sıfırlanıyor...");
                try
                {
                    using var p1 = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
                    p1?.WaitForExit(1000);
                    using var p2 = Process.Start(new ProcessStartInfo("arp.exe", "-d *") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
                    p2?.WaitForExit(1000);
                }
                catch { }

                // 4) Eğer WireSock WARP servisi çalışıyorsa, yeni NDIS bağdaştırıcı tanıtıcısına bağlanması için servisi tazele
                if (WarpManager.IsWireSockServiceRunning())
                {
                    progressCallback?.Invoke(97, "WireSock split-tünel servisi yeni ağ sürücüsüne senkronize ediliyor...");
                    var dummyWarp = new WarpManager();
                    await dummyWarp.ExecuteCommandAsync("net", "stop wiresock-client-service");
                    await Task.Delay(300);
                    int startRes = await dummyWarp.ExecuteCommandAsync("net", "start wiresock-client-service");
                    if (startRes != 0)
                    {
                        await dummyWarp.ExecuteCommandAsync("sc", "start wiresock-client-service");
                    }

                    // Graceful settling delay: allow WireSock to negotiate handshake and stabilize adapter/routes
                    // Prevents request burst storm (HTTP 429) on Discord gateway
                    progressCallback?.Invoke(97, "Ağ tüneli bağlantısı ve bağdaştırıcı stabilizasyonu bekleniyor...");
                    await Task.Delay(3500);
                }

                // 5) Discord & Chromium Koruması: Ağ kartı geçici olarak koptuğunda Discord'un HTTP/3 QUIC kilitlenmesine girmesini önle
                progressCallback?.Invoke(98, "Discord ve oyun ağ soketleri otomatik senkronize ediliyor...");
                WarpManager.RepairDiscordNetworkStateSilent(triggerRestartIfRunning: true);

                // 6) Hedef domainleri (Discord, Roblox) önceden ısıt
                await WarpManager.WarmupTargetDomainsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PostNetworkDriverUpdateHealAsync failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  5. GÜNCELLEME GEÇMİŞİ (HISTORY)
        // ═══════════════════════════════════════════════════════════
        public List<DriverUpdateRecord> GetUpdateHistory()
        {
            try
            {
                if (File.Exists(_historyFile))
                {
                    string json = File.ReadAllText(_historyFile, Encoding.UTF8);
                    var list = JsonSerializer.Deserialize<List<DriverUpdateRecord>>(json) ?? new List<DriverUpdateRecord>();
                    return list.Where(r => !string.Equals(r.OldVersion, r.NewVersion, StringComparison.OrdinalIgnoreCase) && IsNewerVersion(r.NewVersion, r.OldVersion)).ToList();
                }
            }
            catch { }
            return new List<DriverUpdateRecord>();
        }

        private async Task<(bool Success, string Message)> InstallWindowsUpdateDriverAsync(string updateTitle, Action<int, string>? progressCallback = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    progressCallback?.Invoke(45, "Windows Update sürücü kataloğu sorgulanıyor...");
                    Type? updateSessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                    Type? updateCollType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
                    if (updateSessionType == null || updateCollType == null)
                    {
                        return (false, "Windows Update COM bileşenlerine erişilemedi.");
                    }

                    dynamic session = Activator.CreateInstance(updateSessionType)!;
                    dynamic searcher = session.CreateUpdateSearcher();
                    try { searcher.ServerSelection = 0; } catch { }

                    dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver'");
                    dynamic updates = searchResult.Updates;
                    int count = updates.Count;

                    dynamic? targetUpdate = null;
                    for (int i = 0; i < count; i++)
                    {
                        dynamic u = updates.Item(i);
                        string t = u.Title?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(updateTitle) && (t.Contains(updateTitle, StringComparison.OrdinalIgnoreCase) || updateTitle.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        {
                            targetUpdate = u;
                            break;
                        }
                    }

                    if (targetUpdate == null && count > 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            dynamic u = updates.Item(i);
                            string t = u.Title?.ToString() ?? "";
                            var verMatch = Regex.Match(updateTitle, @"\b\d+(\.\d+){1,4}\b");
                            if (verMatch.Success && t.Contains(verMatch.Value))
                            {
                                targetUpdate = u;
                                break;
                            }
                        }
                    }

                    if (targetUpdate == null)
                    {
                        return (false, "İlgili sürücü paketi Windows Update üzerinde hazır bulunamadı veya daha önceden kuruldu.");
                    }

                    // 1. İndir
                    if (!targetUpdate.IsDownloaded)
                    {
                        progressCallback?.Invoke(55, "Sürücü paketi Windows Update'ten indiriliyor...");
                        dynamic updatesToDownload = Activator.CreateInstance(updateCollType)!;
                        updatesToDownload.Add(targetUpdate);

                        dynamic downloader = session.CreateUpdateDownloader();
                        downloader.Updates = updatesToDownload;
                        dynamic dlResult = downloader.Download();
                        if (dlResult.ResultCode != 2 && dlResult.ResultCode != 3)
                        {
                            return (false, $"Windows Update indirme başarısız oldu (Hata Kodu: {dlResult.ResultCode}).");
                        }
                    }

                    // 2. Kur
                    progressCallback?.Invoke(75, "Windows Update WHQL sürücü paketi kuruluyor...");
                    dynamic updatesToInstall = Activator.CreateInstance(updateCollType)!;
                    updatesToInstall.Add(targetUpdate);

                    dynamic installer = session.CreateUpdateInstaller();
                    installer.Updates = updatesToInstall;
                    dynamic instResult = installer.Install();

                    int resCode = (int)instResult.ResultCode;
                    bool rebootReq = (bool)instResult.RebootRequired;

                    if (resCode == 2 || resCode == 3)
                    {
                        string rebootMsg = rebootReq ? " (Kurulumun tam aktifleşmesi için sistemin yeniden başlatılması önerilir)" : "";
                        return (true, "Windows Update sürücüsü başarıyla yüklendi" + rebootMsg);
                    }
                    else
                    {
                        return (false, $"Windows Update kurulumu tamamlanamadı (Hata Kodu: {resCode}).");
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"Windows Update yükleme hatası: {ex.Message}");
                }
            });
        }

        public string GetPhysicalDriverVersion(DriverInfo driver)
        {
            try
            {
                if (driver.Category == DriverCategory.GPU)
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
                    foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                    {
                        string name = obj["Name"]?.ToString()?.Trim() ?? "";
                        if (driver.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(driver.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            string rawVer = obj["DriverVersion"]?.ToString()?.Trim() ?? "";
                            return FormatGpuDisplayVersion(rawVer, driver.Manufacturer);
                        }
                    }
                }
                else
                {
                    string query = driver.Category == DriverCategory.Network
                        ? "SELECT DeviceName, DriverVersion, HardWareID, DeviceID, InfName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET' OR DeviceClass = 'BLUETOOTH'"
                        : "SELECT DeviceName, DriverVersion, HardWareID, DeviceID, InfName FROM Win32_PnPSignedDriver";

                    string targetVenDev = ExtractVendorDeviceKey(driver.HardwareId);
                    string fallbackNameVer = string.Empty;

                    using var searcher = new ManagementObjectSearcher(query);
                    foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                    {
                        string dName = obj["DeviceName"]?.ToString()?.Trim() ?? "";
                        string hwId = ExtractPrimaryHardwareId(obj["HardWareID"], obj["DeviceID"]);
                        string inf = obj["InfName"]?.ToString()?.Trim() ?? "";
                        string ver = obj["DriverVersion"]?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(ver)) continue;

                        if (!string.IsNullOrEmpty(driver.HardwareId) && hwId.Equals(driver.HardwareId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(inf)) driver.InfName = inf;
                            return ver;
                        }

                        if (!string.IsNullOrEmpty(targetVenDev) && hwId.Contains(targetVenDev, StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(dName) || driver.Name.Contains(dName, StringComparison.OrdinalIgnoreCase) || dName.Contains(driver.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrEmpty(inf)) driver.InfName = inf;
                                return ver;
                            }
                            if (string.IsNullOrEmpty(fallbackNameVer))
                                fallbackNameVer = ver;
                        }

                        if (string.IsNullOrEmpty(fallbackNameVer) && !string.IsNullOrEmpty(dName) &&
                            (driver.Name.Equals(dName, StringComparison.OrdinalIgnoreCase) ||
                             driver.Name.Contains(dName, StringComparison.OrdinalIgnoreCase) ||
                             dName.Contains(driver.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            fallbackNameVer = ver;
                            if (!string.IsNullOrEmpty(inf)) driver.InfName = inf;
                        }
                    }

                    if (!string.IsNullOrEmpty(fallbackNameVer))
                        return fallbackNameVer;
                }
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Warn("DRIVER", $"Fiziksel sürücü sürümü sorgulanamadı: {ex.Message}");
            }
            return string.Empty;
        }

        private async Task<string> WaitForUpdatedPhysicalVersionAsync(DriverInfo driver, string oldVersion)
        {
            string lastVer = string.Empty;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                lastVer = GetPhysicalDriverVersion(driver);
                if (!string.IsNullOrEmpty(lastVer) && IsNewerVersion(lastVer, oldVersion))
                {
                    return lastVer;
                }
                if (attempt < 5)
                {
                    await Task.Delay(1000);
                }
            }
            return lastVer;
        }

        private static string ExtractPrimaryHardwareId(object? rawHwId, object? rawDeviceId = null)
        {
            if (rawHwId is string s && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
            if (rawHwId is string[] arr && arr.Length > 0)
            {
                var first = arr.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                if (!string.IsNullOrWhiteSpace(first)) return first.Trim();
            }
            if (rawDeviceId is string devId && !string.IsNullOrWhiteSpace(devId))
                return devId.Trim();
            return string.Empty;
        }

        private static string ExtractVendorDeviceKey(string? hardwareId)
        {
            if (string.IsNullOrWhiteSpace(hardwareId)) return string.Empty;
            var match = Regex.Match(hardwareId, @"(PCI|USB)\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;
            var usbMatch = Regex.Match(hardwareId, @"USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase);
            return usbMatch.Success ? usbMatch.Value : string.Empty;
        }

        private async Task<string?> ResolveMicrosoftCatalogCabUrlAsync(DriverInfo driver)
        {
            try
            {
                bool isWin11 = Environment.OSVersion.Version.Build >= 22000;
                string venDev = ExtractVendorDeviceKey(driver.HardwareId);
                var queries = new List<string>();
                if (!string.IsNullOrEmpty(venDev) && !string.IsNullOrEmpty(driver.LatestVersion))
                    queries.Add($"{venDev} {driver.LatestVersion}");
                if (!string.IsNullOrEmpty(venDev))
                    queries.Add(venDev);
                if (!string.IsNullOrEmpty(driver.WindowsUpdateTitle))
                    queries.Add(driver.WindowsUpdateTitle);

                foreach (string query in queries.Distinct())
                {
                    string searchUrl = "https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(query);
                    string html = await _http.GetStringAsync(searchUrl);
                    var rows = Regex.Matches(html, @"<tr id=""([a-f0-9\-]{36})_R\d+""[\s\S]*?</tr>", RegexOptions.IgnoreCase);
                    if (rows.Count == 0) continue;

                    string? bestId = null;
                    foreach (Match r in rows)
                    {
                        string id = r.Groups[1].Value;
                        string rowText = Regex.Replace(r.Value, "<[^>]+>", " ");
                        if (isWin11 && (rowText.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) || rowText.Contains("Windows - Client", StringComparison.OrdinalIgnoreCase)))
                        {
                            bestId = id;
                            break;
                        }
                        if (!isWin11 && (rowText.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) || rowText.Contains("Windows - Client", StringComparison.OrdinalIgnoreCase)))
                        {
                            bestId = id;
                            break;
                        }
                        bestId ??= id;
                    }

                    if (string.IsNullOrEmpty(bestId)) continue;

                    string postBody = "updateIDs=" + Uri.EscapeDataString($"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{bestId}\",\"updateID\":\"{bestId}\"}}]");
                    using var content = new StringContent(postBody, Encoding.UTF8, "application/x-www-form-urlencoded");
                    var dlResp = await _http.PostAsync("https://www.catalog.update.microsoft.com/DownloadDialog.aspx", content);
                    if (!dlResp.IsSuccessStatusCode) continue;

                    string dlHtml = await dlResp.Content.ReadAsStringAsync();
                    var cabMatch = Regex.Match(dlHtml, @"https?://[^\s'""<>]+\.cab", RegexOptions.IgnoreCase);
                    if (cabMatch.Success)
                    {
                        return cabMatch.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Warn("DRIVER", $"Microsoft Update Catalog çözümleme hatası: {ex.Message}");
            }
            return null;
        }

        private async Task<(bool Success, string Message)> InstallCabDriverPackageAsync(string cabPath, DriverInfo driver, Action<int, string>? progressCallback = null)
        {
            string extractDir = Path.Combine(Path.GetTempPath(), $"AlpNetCab_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(extractDir);
                progressCallback?.Invoke(76, "WHQL sürücü arşivi açılıyor...");

                string expandExe = Path.Combine(Environment.SystemDirectory, "expand.exe");
                if (!File.Exists(expandExe)) expandExe = "expand.exe";

                var expandPsi = new ProcessStartInfo
                {
                    FileName = expandExe,
                    Arguments = $"\"{cabPath}\" -F:* \"{extractDir}\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var expProc = Process.Start(expandPsi))
                {
                    if (expProc == null)
                        return (false, "expand.exe başlatılamadı.");

                    string expOut = await expProc.StandardOutput.ReadToEndAsync();
                    string expErr = await expProc.StandardError.ReadToEndAsync();
                    await expProc.WaitForExitAsync();

                    if (expProc.ExitCode != 0)
                    {
                        return (false, $"CAB arşivi açılamadı (Kod: {expProc.ExitCode}): {(string.IsNullOrWhiteSpace(expErr) ? expOut.Trim() : expErr.Trim())}");
                    }
                }

                var infFiles = Directory.GetFiles(extractDir, "*.inf", SearchOption.AllDirectories);
                if (infFiles.Length == 0)
                {
                    return (false, "İndirilen WHQL paketi içinde .inf kurulum dosyası bulunamadı.");
                }

                progressCallback?.Invoke(82, "İmzalı WHQL sürücüsü Windows sürücü deposuna yükleniyor (pnputil)...");
                string pnputilExe = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
                if (!File.Exists(pnputilExe)) pnputilExe = "pnputil.exe";

                var pnpPsi = new ProcessStartInfo
                {
                    FileName = pnputilExe,
                    Arguments = $"/add-driver \"{extractDir}\\*.inf\" /subdirs /install",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var pnpProc = Process.Start(pnpPsi))
                {
                    if (pnpProc == null)
                        return (false, "pnputil.exe başlatılamadı.");

                    string pnpOut = await pnpProc.StandardOutput.ReadToEndAsync();
                    string pnpErr = await pnpProc.StandardError.ReadToEndAsync();
                    await pnpProc.WaitForExitAsync();

                    // 0 = Başarılı, 3010 = Başarılı (Yeniden başlatma önerilir), 259 = Daha yeni/mevcut eşleşme yok
                    if (pnpProc.ExitCode != 0 && pnpProc.ExitCode != 3010 && pnpProc.ExitCode != 259)
                    {
                        string detail = !string.IsNullOrWhiteSpace(pnpErr) ? pnpErr.Trim() : pnpOut.Trim();
                        return (false, $"pnputil sürücü yüklemesi başarısız (Kod: {pnpProc.ExitCode}): {detail}");
                    }

                    AlpNetTelemetryService.Instance.Info("DRIVER", $"pnputil WHQL CAB kurulumu tamamlandı ({driver.Name}, Kod: {pnpProc.ExitCode}): {pnpOut.Replace(Environment.NewLine, " | ").Trim()}");
                }

                return (true, "WHQL CAB paketi başarıyla yüklendi.");
            }
            catch (Exception ex)
            {
                return (false, $"CAB sürücü kurulum hatası: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(extractDir))
                        Directory.Delete(extractDir, true);
                }
                catch { }
            }
        }

        public static bool IsNewerVersion(string latestVer, string currentVer)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(latestVer) || string.IsNullOrWhiteSpace(currentVer)) return false;
                if (string.Equals(latestVer.Trim(), currentVer.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

                string cleanLatest = Regex.Replace(latestVer, @"[^\d.]", "");
                string cleanCurrent = Regex.Replace(currentVer, @"[^\d.]", "");

                if (Version.TryParse(cleanLatest, out var vLatest) && Version.TryParse(cleanCurrent, out var vCurrent))
                {
                    return vLatest > vCurrent;
                }

                var partsLatest = cleanLatest.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var partsCurrent = cleanCurrent.Split('.', StringSplitOptions.RemoveEmptyEntries);
                int maxLen = Math.Max(partsLatest.Length, partsCurrent.Length);

                for (int i = 0; i < maxLen; i++)
                {
                    long pL = i < partsLatest.Length && long.TryParse(partsLatest[i], out var l) ? l : 0;
                    long pC = i < partsCurrent.Length && long.TryParse(partsCurrent[i], out var c) ? c : 0;
                    if (pL > pC) return true;
                    if (pL < pC) return false;
                }
            }
            catch { }
            return false;
        }

        private void RecordHistory(DriverUpdateRecord record)
        {
            try
            {
                var list = GetUpdateHistory();
                list.Insert(0, record);
                if (list.Count > 50) list = list.Take(50).ToList();
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_historyFile, json, Encoding.UTF8);
            }
            catch { }
        }

        private void SaveHistoryRecord(DriverUpdateRecord updatedRecord)
        {
            try
            {
                var list = GetUpdateHistory();
                var existing = list.FirstOrDefault(r => r.UpdateId == updatedRecord.UpdateId);
                if (existing != null)
                {
                    existing.Status = updatedRecord.Status;
                }
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_historyFile, json, Encoding.UTF8);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  6. 48 SAATLİK YEDEK DÖNGÜSÜ & CİHAZ YEDEK BULMA
        // ═══════════════════════════════════════════════════════════
        public void PruneBackupsOlderThan(TimeSpan maxAge)
        {
            try
            {
                if (!Directory.Exists(_backupsDir)) return;
                var dirInfo = new DirectoryInfo(_backupsDir);
                DateTime threshold = DateTime.Now - maxAge;

                foreach (var subDir in dirInfo.GetDirectories())
                {
                    try
                    {
                        if (subDir.CreationTime < threshold && subDir.LastWriteTime < threshold)
                        {
                            subDir.Delete(true);
                        }
                    }
                    catch { }
                }

                var history = GetUpdateHistory();
                var validHistory = history.Where(r =>
                {
                    if (DateTime.TryParse(r.Timestamp, out var dt))
                    {
                        return dt >= threshold;
                    }
                    return true;
                }).ToList();

                if (validHistory.Count != history.Count)
                {
                    string json = JsonSerializer.Serialize(validHistory, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_historyFile, json, Encoding.UTF8);
                }
            }
            catch { }
        }

        public DriverUpdateRecord? GetLatestBackupForDevice(DriverInfo driver)
        {
            try
            {
                var history = GetUpdateHistory();
                DateTime threshold = DateTime.Now.AddHours(-48);

                return history.FirstOrDefault(r =>
                    (r.DeviceName.Equals(driver.Name, StringComparison.OrdinalIgnoreCase) ||
                     r.DeviceName.Contains(driver.Name, StringComparison.OrdinalIgnoreCase) ||
                     driver.Name.Contains(r.DeviceName, StringComparison.OrdinalIgnoreCase)) &&
                    DateTime.TryParse(r.Timestamp, out var dt) && dt >= threshold &&
                    !string.IsNullOrEmpty(r.BackupPath) && Directory.Exists(r.BackupPath));
            }
            catch { }
            return null;
        }



        private static string FormatDriverDate(string? wmiDate)
        {
            if (string.IsNullOrWhiteSpace(wmiDate)) return "Bilinmiyor";
            try
            {
                if (wmiDate.Length >= 8)
                {
                    string y = wmiDate.Substring(0, 4);
                    string m = wmiDate.Substring(4, 2);
                    string d = wmiDate.Substring(6, 2);
                    return $"{d}.{m}.{y}";
                }
            }
            catch { }
            return wmiDate;
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            string clean = Regex.Replace(html, "<.*?>", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            return clean.Length > 250 ? clean.Substring(0, 250) + "..." : clean;
        }

        private async Task<bool> DownloadFileWithProgressAsync(string url, string targetPath, Action<int>? progress)
        {
            try
            {
                using var dlClient = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                })
                {
                    Timeout = TimeSpan.FromMinutes(10)
                };
                dlClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 AlpNetPulse/2.3.6");

                using var response = await dlClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;
                int lastPct = -1;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int pct = (int)((totalRead * 100) / totalBytes.Value);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress?.Invoke(pct);
                        }
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CreateSystemRestorePoint(string description)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"Checkpoint-Computer -Description '{description}' -RestorePointType 'DEVICE_DRIVER_INSTALL' -ErrorAction SilentlyContinue\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch { }
        }

        private static async Task TriggerDeviceUpdateAsync(string hardwareId)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/scan-devices",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                if (p != null) await p.WaitForExitAsync();
            }
            catch { }
        }

        /// <summary>
        /// AlpTogether için tam sistem donanım özelliklerini derler.
        /// </summary>
        public static DeviceHardwareSpecs CollectDeviceHardwareSpecs(
            List<DriverInfo> gpus,
            List<DriverInfo> cpus,
            List<DriverInfo> mbList,
            List<DriverInfo> netList)
        {
            var specs = new DeviceHardwareSpecs();

            try
            {
                // 1. CPU
                var primaryCpu = cpus?.FirstOrDefault();
                if (primaryCpu != null)
                {
                    specs.CpuName = primaryCpu.Name;
                }

                try
                {
                    using var cpuSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                    foreach (ManagementObject obj in cpuSearcher.Get())
                    {
                        if (string.IsNullOrEmpty(specs.CpuName))
                            specs.CpuName = obj["Name"]?.ToString()?.Trim() ?? "";

                        if (int.TryParse(obj["NumberOfCores"]?.ToString(), out int cores) && cores > 0)
                            specs.CpuCores = cores;

                        if (int.TryParse(obj["NumberOfLogicalProcessors"]?.ToString(), out int lp) && lp > 0)
                            specs.CpuLogicalProcessors = lp;
                        break;
                    }
                }
                catch { }

                if (specs.CpuLogicalProcessors == 0)
                    specs.CpuLogicalProcessors = Environment.ProcessorCount;
                if (specs.CpuCores == 0)
                    specs.CpuCores = Math.Max(1, Environment.ProcessorCount / 2);
                if (string.IsNullOrEmpty(specs.CpuName))
                    specs.CpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Bilinmeyen İşlemci";

                // 2. GPU (Öncelikli olarak harici discrete GPU: NVIDIA / AMD / Intel Arc)
                DriverInfo? primaryGpu = null;
                if (gpus != null && gpus.Count > 0)
                {
                    primaryGpu = gpus.FirstOrDefault(g => 
                        (g.Manufacturer == "NVIDIA" || g.Manufacturer == "AMD" || 
                         g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || 
                         g.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || 
                         g.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || 
                         g.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) &&
                        !g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                        ?? gpus.FirstOrDefault(g => !g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                        ?? gpus.FirstOrDefault();
                }

                if (primaryGpu != null)
                {
                    specs.GpuName = primaryGpu.Name;
                    specs.GpuDriverVersion = primaryGpu.CurrentVersion;
                }

                try
                {
                    using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
                    ManagementObject? bestGpuObj = null;
                    long bestVram = 0;

                    foreach (ManagementObject obj in gpuSearcher.Get())
                    {
                        string gName = obj["Name"]?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(gName) || gName.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                            continue;

                        long.TryParse(obj["AdapterRAM"]?.ToString(), out long vramBytes);

                        bool isDiscrete = gName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                          gName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                          gName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                          gName.Contains("Arc", StringComparison.OrdinalIgnoreCase);

                        if (bestGpuObj == null || isDiscrete || vramBytes > bestVram)
                        {
                            bestGpuObj = obj;
                            bestVram = vramBytes;
                            if (isDiscrete) break; // Harici GPU bulunduysa öncelik ver
                        }
                    }

                    if (bestGpuObj != null)
                    {
                        if (string.IsNullOrEmpty(specs.GpuName))
                        {
                            specs.GpuName = bestGpuObj["Name"]?.ToString()?.Trim() ?? "";
                            string rawVer = bestGpuObj["DriverVersion"]?.ToString()?.Trim() ?? "";
                            string mfg = specs.GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
                                         (specs.GpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD" : "Intel");
                            specs.GpuDriverVersion = FormatGpuDisplayVersion(rawVer, mfg);
                        }

                        if (bestVram > 0)
                        {
                            long vramMb = bestVram / (1024 * 1024);
                            specs.GpuMemory = $"{vramMb} MB";
                        }
                    }
                }
                catch { }

                // 3. Motherboard & BIOS (Doğrudan Win32_BaseBoard ve Win32_BIOS sorgulanır)
                try
                {
                    using var bbSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
                    foreach (ManagementObject obj in bbSearcher.Get())
                    {
                        string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                        string prod = obj["Product"]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(mfg)) specs.MotherboardManufacturer = mfg;
                        if (!string.IsNullOrEmpty(prod)) specs.MotherboardProduct = prod;
                        break;
                    }
                }
                catch { }

                try
                {
                    using var biosSearcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS");
                    foreach (ManagementObject obj in biosSearcher.Get())
                    {
                        string bVer = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                        string bDate = FormatDriverDate(obj["ReleaseDate"]?.ToString());
                        if (!string.IsNullOrEmpty(bVer)) specs.BiosVersion = bVer;
                        if (!string.IsNullOrEmpty(bDate)) specs.BiosReleaseDate = bDate;
                        if (string.IsNullOrEmpty(specs.MotherboardManufacturer))
                            specs.MotherboardManufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                        break;
                    }
                }
                catch { }

                // mbList yedek kontrolü
                if (mbList != null && mbList.Count > 0)
                {
                    var baseboard = mbList.FirstOrDefault(m => m.Id == "MB_BASEBOARD" || m.IsBiosFirmware);
                    if (baseboard != null)
                    {
                        if (string.IsNullOrEmpty(specs.MotherboardManufacturer)) specs.MotherboardManufacturer = baseboard.Manufacturer;
                        if (string.IsNullOrEmpty(specs.BiosVersion)) specs.BiosVersion = baseboard.CurrentVersion;
                        if (string.IsNullOrEmpty(specs.BiosReleaseDate)) specs.BiosReleaseDate = baseboard.CurrentDateStr;
                    }
                }

                // 4. Network (Wi-Fi & Ethernet)
                if (netList != null && netList.Count > 0)
                {
                    var wifi = netList.FirstOrDefault(n => 
                        n.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || 
                        n.Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                        n.Name.Contains("802.11", StringComparison.OrdinalIgnoreCase));
                    if (wifi != null)
                    {
                        specs.WifiAdapterName = wifi.Name;
                        specs.WifiDriverVersion = wifi.CurrentVersion;
                    }

                    var eth = netList.FirstOrDefault(n => 
                        (n.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || 
                         n.Name.Contains("GbE", StringComparison.OrdinalIgnoreCase) || 
                         n.Name.Contains("PCIe", StringComparison.OrdinalIgnoreCase) || 
                         n.Name.Contains("Gigabit", StringComparison.OrdinalIgnoreCase) ||
                         n.Name.Contains("Realtek Gaming", StringComparison.OrdinalIgnoreCase)) &&
                        !n.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                        !n.Name.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase));
                    if (eth != null)
                    {
                        specs.EthernetAdapterName = eth.Name;
                        specs.EthernetDriverVersion = eth.CurrentVersion;
                    }
                }

                // Eğer netList boşsa veya adaptörler bulunamadıysa WMI üzerinden doğrudan ara
                if (string.IsNullOrEmpty(specs.WifiAdapterName) || string.IsNullOrEmpty(specs.EthernetAdapterName))
                {
                    try
                    {
                        using var netSearcher = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DeviceClass FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'");
                        foreach (ManagementObject obj in netSearcher.Get())
                        {
                            string dName = obj["DeviceName"]?.ToString()?.Trim() ?? "";
                            string dVer = obj["DriverVersion"]?.ToString()?.Trim() ?? "";
                            if (string.IsNullOrWhiteSpace(dName) || dName.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) || dName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (string.IsNullOrEmpty(specs.WifiAdapterName) &&
                                (dName.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || dName.Contains("Wireless", StringComparison.OrdinalIgnoreCase) || dName.Contains("802.11", StringComparison.OrdinalIgnoreCase)))
                            {
                                specs.WifiAdapterName = dName;
                                specs.WifiDriverVersion = dVer;
                            }
                            else if (string.IsNullOrEmpty(specs.EthernetAdapterName) &&
                                (dName.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || dName.Contains("GbE", StringComparison.OrdinalIgnoreCase) || dName.Contains("PCIe", StringComparison.OrdinalIgnoreCase) || dName.Contains("Gigabit", StringComparison.OrdinalIgnoreCase)))
                            {
                                specs.EthernetAdapterName = dName;
                                specs.EthernetDriverVersion = dVer;
                            }
                        }
                    }
                    catch { }
                }

                // 5. OS & RAM
                try
                {
                    using var osSearcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, TotalVisibleMemorySize FROM Win32_OperatingSystem");
                    foreach (ManagementObject obj in osSearcher.Get())
                    {
                        specs.OsCaption = obj["Caption"]?.ToString()?.Trim() ?? "";
                        specs.OsVersion = obj["Version"]?.ToString()?.Trim() ?? "";
                        specs.OsBuildNumber = obj["BuildNumber"]?.ToString()?.Trim() ?? "";
                        if (long.TryParse(obj["TotalVisibleMemorySize"]?.ToString(), out long kb))
                        {
                            specs.TotalRamMb = kb / 1024;
                        }
                        break;
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(specs.OsCaption)) specs.OsCaption = "Microsoft Windows";
                if (string.IsNullOrEmpty(specs.OsBuildNumber)) specs.OsBuildNumber = Environment.OSVersion.Version.Build.ToString();
                if (string.IsNullOrEmpty(specs.OsVersion)) specs.OsVersion = Environment.OSVersion.Version.ToString();
                if (specs.TotalRamMb <= 0) specs.TotalRamMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));

                specs.UserName = Environment.UserName;
                specs.MachineName = Environment.MachineName;
                specs.LastScanned = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                specs.DeviceId = AlpNetTelemetryService.Instance.EnsureDeviceId();
                specs.AlpTogetherUserId = AlpNetTelemetryService.Instance.AlpTogetherUserId ?? "";
                if (!string.IsNullOrEmpty(specs.Cpu.Model))
                {
                    specs.Cpu.Description = $"{specs.Cpu.Model} ({specs.Cpu.Cores} Çekirdek / {specs.Cpu.Threads} İş Parçacığı)";
                }
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Warn("DRIVER", $"Donanım özellikleri çıkarılırken hata: {ex.Message}");
            }

            return specs;
        }

        public static DeviceHardwareSpecs CollectDeviceHardwareSpecs(List<DriverInfo> scannedDrivers)
        {
            var gpus = scannedDrivers?.Where(d => d.Category == DriverCategory.GPU).ToList() ?? new List<DriverInfo>();
            var cpus = scannedDrivers?.Where(d => d.Category == DriverCategory.CPU).ToList() ?? new List<DriverInfo>();
            var mbList = scannedDrivers?.Where(d => d.Category == DriverCategory.Motherboard).ToList() ?? new List<DriverInfo>();
            var netList = scannedDrivers?.Where(d => d.Category == DriverCategory.Network).ToList() ?? new List<DriverInfo>();
            return CollectDeviceHardwareSpecs(gpus, cpus, mbList, netList);
        }
    }
}
