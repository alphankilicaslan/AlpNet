using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AlpNetWebSetup
{
    public partial class MainWindow : Window
    {
        private const string AppStateUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/appstate.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
        private const string DotNet9DesktopRuntimeUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe";

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Any(a => a.Equals("/auto", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-auto", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-silent", StringComparison.OrdinalIgnoreCase)))
            {
                BtnLaunch_Click(this, new RoutedEventArgs());
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            BtnLaunch.Visibility = Visibility.Collapsed;
            PanelProgress.Visibility = Visibility.Visible;
            TxtStatus.Text = "Sunucuya bağlanılıyor...";

            try
            {
                // 1. Fetch update URL from Firebase
                AppState state = null;
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var response = await client.GetAsync(AppStateUrl);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    var serializer = new JavaScriptSerializer();
                    state = serializer.Deserialize<AppState>(json);
                }

                if (state == null || string.IsNullOrEmpty(state.updateUrl))
                {
                    MessageBox.Show("Sunucudan indirme bağlantısı alınamadı.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetUI();
                    return;
                }

                // 2. .NET 9 Desktop Runtime kontrolü ve gerekirse otomatik kurulumu
                if (!IsDotNet9DesktopRuntimeInstalled())
                {
                    TxtStatus.Text = "Gerekli .NET 9 bileşeni indiriliyor...";
                    PbDownload.Value = 0;

                    string tempRuntimeInstaller = Path.Combine(Path.GetTempPath(), "dotnet9_desktop_runtime_x64.exe");
                    bool runtimeDownloaded = await DownloadFileWithProgressAsync(
                        DotNet9DesktopRuntimeUrl,
                        tempRuntimeInstaller,
                        (percent) =>
                        {
                            PbDownload.Value = percent;
                            TxtStatus.Text = $".NET 9 Bileşeni İndiriliyor... %{percent:0}";
                        }
                    );

                    if (runtimeDownloaded && File.Exists(tempRuntimeInstaller))
                    {
                        TxtStatus.Text = ".NET 9 Bileşeni Kuruluyor (Lütfen bekleyin)...";
                        PbDownload.Value = 100;

                        await Task.Run(() =>
                        {
                            try
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = tempRuntimeInstaller,
                                    Arguments = "/install /quiet /norestart",
                                    UseShellExecute = true,
                                    WindowStyle = ProcessWindowStyle.Hidden
                                };
                                var proc = Process.Start(psi);
                                proc?.WaitForExit(180000); // 3 dakika timeout
                            }
                            catch { }
                            finally
                            {
                                try { File.Delete(tempRuntimeInstaller); } catch { }
                            }
                        });
                    }
                }

                // 3. Kurulum klasörünü belirle (%LOCALAPPDATA%\AlpNet)
                string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet");
                if (!Directory.Exists(installDir))
                    Directory.CreateDirectory(installDir);

                // Eski temp ve old dosyaları temizle
                CleanupResidualFiles(installDir);

                string targetExe = Path.Combine(installDir, "AlpNet.Pulse.exe");
                string tempExe = Path.Combine(installDir, "AlpNet.Pulse.tmp");

                string translatorUrl = "https://github.com/alphankilicaslan/AlpNet/releases/download/Update/translator.exe";
                string targetTranslator = Path.Combine(installDir, "translator.exe");
                string tempTranslator = Path.Combine(installDir, "translator.tmp");

                // 4. AlpNet.Pulse.exe İndir
                TxtStatus.Text = "İndiriliyor (1/2)... %0";
                PbDownload.Value = 0;
                bool pulseDownloaded = await DownloadFileWithProgressAsync(
                    state.updateUrl,
                    tempExe,
                    (percent) =>
                    {
                        PbDownload.Value = percent;
                        TxtStatus.Text = $"İndiriliyor (1/2)... %{percent:0}";
                    }
                );

                if (!pulseDownloaded)
                {
                    throw new Exception("AlpNet.Pulse.exe indirilemedi.");
                }

                // 5. translator.exe İndir
                TxtStatus.Text = "İndiriliyor (2/2)... %0";
                PbDownload.Value = 0;
                bool translatorDownloaded = await DownloadFileWithProgressAsync(
                    translatorUrl,
                    tempTranslator,
                    (percent) =>
                    {
                        PbDownload.Value = percent;
                        TxtStatus.Text = $"İndiriliyor (2/2)... %{percent:0}";
                    }
                );

                if (!translatorDownloaded)
                {
                    throw new Exception("translator.exe indirilemedi.");
                }

                // 6. Çalışan eski süreçleri kapat
                KillExistingProcesses();

                // 7. Dosyaları güvenli bir şekilde asıl konumlarına taşı
                ReplaceFileSafely(tempExe, targetExe);
                ReplaceFileSafely(tempTranslator, targetTranslator);

                // 7.1. Cloudflare WARP tünel profilini hazırla (%LOCALAPPDATA%\AlpNet\res\wgcf-profile.conf)
                try
                {
                    string resDir = Path.Combine(installDir, "res");
                    if (!Directory.Exists(resDir))
                        Directory.CreateDirectory(resDir);

                    string profilePath = Path.Combine(resDir, "wgcf-profile.conf");
                    // Eski çakışan ortak anahtarlı profili temizle ki AlpNet Pulse ilk açılışta cihaza özel temiz profil üretsin
                    if (File.Exists(profilePath))
                    {
                        try
                        {
                            string pText = File.ReadAllText(profilePath);
                            if (pText.Contains("ID6Va+b6e5vusYdSh8PkhB+Z8gOVPZFtq2/fni+1K1Q="))
                            {
                                File.Delete(profilePath);
                                string accPath = Path.Combine(resDir, "wgcf-account.toml");
                                if (File.Exists(accPath)) File.Delete(accPath);
                            }
                        }
                        catch { }
                    }

                    // HTTP/3 QUIC politikasını tarayıcılar için etkinleştir
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
                            using (var cuKey = Registry.CurrentUser.CreateSubKey(path))
                            {
                                cuKey?.SetValue("QuicAllowed", 1, RegistryValueKind.DWord);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // 8. Masaüstü Kısayolu Oluştur (Temiz COM nesnesiyle, PowerShell kullanmadan)
                TxtStatus.Text = "Kısayol oluşturuluyor...";
                CreateDesktopShortcut(targetExe, installDir, "AlpNet Pulse");

                TxtStatus.Text = "Başlatılıyor...";
                PbDownload.Value = 100;

                // 9. Uygulamayı başlat
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });

                await Task.Delay(500);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("İndirme ve kurulum sırasında hata oluştu:\n\n" + ex.Message, "Kurulum Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetUI();
            }
        }

        private static bool IsDotNet9DesktopRuntimeInstalled()
        {
            try
            {
                // Kontrol 1: 64-bit Registry View
                try
                {
                    using (var baseKey64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var key = baseKey64.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                    {
                        if (key != null)
                        {
                            foreach (var name in key.GetValueNames())
                            {
                                if (name.StartsWith("9.", StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                    }
                }
                catch { }

                // Kontrol 2: 32-bit / WOW64 Registry View
                try
                {
                    using (var baseKey32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    using (var key = baseKey32.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                    {
                        if (key != null)
                        {
                            foreach (var name in key.GetValueNames())
                            {
                                if (name.StartsWith("9.", StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                    }
                }
                catch { }

                // Kontrol 3: WOW6432Node Registry
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                    {
                        if (key != null)
                        {
                            foreach (var name in key.GetValueNames())
                            {
                                if (name.StartsWith("9.", StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                    }
                }
                catch { }

                // Kontrol 4: Fiziksel Klasör Kontrolleri (ProgramW6432, ProgramFiles, ProgramFiles(x86))
                var candidateDirs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string progW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
                if (!string.IsNullOrEmpty(progW6432)) candidateDirs.Add(progW6432);

                string progFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                if (!string.IsNullOrEmpty(progFiles)) candidateDirs.Add(progFiles);

                string specialProgFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(specialProgFiles)) candidateDirs.Add(specialProgFiles);

                string progFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                if (!string.IsNullOrEmpty(progFilesX86)) candidateDirs.Add(progFilesX86);

                foreach (var baseDir in candidateDirs)
                {
                    string sharedFolder = Path.Combine(baseDir, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                    if (Directory.Exists(sharedFolder))
                    {
                        var dirs = Directory.GetDirectories(sharedFolder, "9.*");
                        if (dirs.Length > 0)
                            return true;
                    }
                }

                // Kontrol 5: dotnet --list-runtimes komut satırı sorgusu
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dotnet.exe",
                        Arguments = "--list-runtimes",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var proc = Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            string output = proc.StandardOutput.ReadToEnd();
                            proc.WaitForExit(3000);
                            if (output.IndexOf("Microsoft.WindowsDesktop.App 9.", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return true;
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }

            return false;
        }

        private static async Task<bool> DownloadFileWithProgressAsync(string url, string destinationPath, Action<double> onProgress)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }

                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canReportProgress = totalBytes > 0;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            var buffer = new byte[81920];
                            long totalRead = 0L;
                            int bytesRead;

                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                            {
                                await fs.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                if (canReportProgress)
                                {
                                    double percentage = Math.Min(100.0, (double)totalRead / totalBytes * 100.0);
                                    onProgress?.Invoke(percentage);
                                }
                            }
                        }
                    }
                }
                return true;
            }
            catch
            {
                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }
                return false;
            }
        }

        private static void ReplaceFileSafely(string sourceFile, string destinationFile)
        {
            if (!File.Exists(sourceFile)) return;

            int retries = 5;
            while (retries > 0)
            {
                try
                {
                    if (File.Exists(destinationFile))
                    {
                        try
                        {
                            File.Delete(destinationFile);
                        }
                        catch
                        {
                            string oldBackup = destinationFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".old";
                            try { File.Move(destinationFile, oldBackup); } catch { }
                        }
                    }

                    if (File.Exists(destinationFile))
                    {
                        File.Copy(sourceFile, destinationFile, true);
                        try { File.Delete(sourceFile); } catch { }
                    }
                    else
                    {
                        File.Move(sourceFile, destinationFile);
                    }
                    break;
                }
                catch
                {
                    retries--;
                    if (retries == 0) throw;
                    Thread.Sleep(300);
                }
            }
        }

        private static void CleanupResidualFiles(string installDir)
        {
            try
            {
                var files = Directory.GetFiles(installDir, "*.*");
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLower();
                    if (ext == ".tmp" || ext == ".old")
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        private static void KillExistingProcesses()
        {
            string[] procNames = { "AlpNet.Pulse", "AlpNetPulse", "translator", "AlpNet" };
            foreach (var name in procNames)
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(1500);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private static void CreateDesktopShortcut(string targetPath, string workingDir, string shortcutName)
        {
            try
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcutPath = Path.Combine(desktopDir, shortcutName + ".lnk");

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                    if (shortcut != null)
                    {
                        Type shortcutType = shortcut.GetType();
                        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
                        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "AlpNet Pulse" });
                        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    }
                }
            }
            catch { }
        }

        private void ResetUI()
        {
            BtnLaunch.Visibility = Visibility.Visible;
            PanelProgress.Visibility = Visibility.Collapsed;
            PbDownload.Value = 0;
            TxtStatus.Text = "";
        }

        public class AppState
        {
            public string updateUrl { get; set; }
        }
    }
}