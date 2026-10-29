using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace AlpNetPulse
{
    public partial class App : System.Windows.Application
    {
        private Mutex? _mutex;

        public static void ReleaseSingleInstanceMutex()
        {
            try
            {
                if (System.Windows.Application.Current is App app && app._mutex != null)
                {
                    AlpNetTelemetryService.Instance.Info("SİSTEM", "Tekil örnek (SingleInstance) Mutex serbest bırakılıyor...");
                    try { app._mutex.ReleaseMutex(); } catch { }
                    try { app._mutex.Dispose(); } catch { }
                    app._mutex = null;
                }
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                AlpNetTelemetryService.Instance.HookGlobalExceptions();
                AppDomain.CurrentDomain.ProcessExit += (s, ev) => SafeGlobalShutdown();
                Microsoft.Win32.SystemEvents.SessionEnding += (s, ev) => SafeGlobalShutdown();
                AlpNetTelemetryService.Instance.Info("BAŞLATMA", $"Uygulama açılıyor... PID: {Process.GetCurrentProcess().Id}, Argümanlar: {string.Join(" ", e.Args)}");
            }
            catch { }

            string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string exeName = Path.GetFileName(currentExe).ToLower();
            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet");
            string installExe = Path.Combine(installDir, "AlpNet.Pulse.exe");

            // Check if this instance is running as the installer
            bool isInstaller = !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(installExe), StringComparison.OrdinalIgnoreCase) && 
                               (exeName.Contains("setup") || exeName.Contains("kur") || exeName.Contains("yükle"));

            if (isInstaller)
            {
                try
                {
                    AlpNetTelemetryService.Instance.Info("KURULUM", $"Kurulum modu algılandı. Hedef: {installExe}");

                    // 1. Kill any running instance of AlpNet / AlpNetPulse / AlpNet.Pulse to avoid file lock
                    foreach (var name in new[] { "AlpNet", "AlpNetPulse", "AlpNet.Pulse" })
                    {
                        foreach (var proc in Process.GetProcessesByName(name))
                        {
                            try
                            {
                                proc.Kill();
                                proc.WaitForExit(2000);
                            }
                            catch { }
                        }
                    }

                    // Give Windows some time to release file locks
                    Thread.Sleep(600);

                    // 2. Create installation directory
                    if (!Directory.Exists(installDir))
                    {
                        Directory.CreateDirectory(installDir);
                    }

                    // 3. Copy executable
                    int retries = 8;
                    while (retries > 0)
                    {
                        try
                        {
                            File.Copy(currentExe, installExe, true);
                            break;
                        }
                        catch
                        {
                            retries--;
                            if (retries == 0) throw;
                            Thread.Sleep(300);
                        }
                    }

                    // 4. Create Desktop Shortcut via PowerShell
                    string psCommand = $"$desktop = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop); $shortcutPath = [System.IO.Path]::Combine($desktop, 'AlpNet Pulse.lnk'); $wsh = New-Object -ComObject WScript.Shell; $shortcut = $wsh.CreateShortcut($shortcutPath); $shortcut.TargetPath = '{installExe}'; $shortcut.IconLocation = '{installExe},0'; $shortcut.Save()";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand.Replace("\"", "\\\"")}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    
                    var psProc = Process.Start(psi);
                    psProc?.WaitForExit(3000);

                    // 5. Run the installed app
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = installExe,
                        UseShellExecute = true
                    });
                    // Exit the setup process
                    Shutdown(0);
                    return;
                }
                catch (Exception ex)
                {
                    AlpNetTelemetryService.Instance.Error("KURULUM", "Kurulum hatası: " + ex.Message, ex);
                    System.Windows.MessageBox.Show("Kurulum hatası: " + ex.Message + "\nLütfen çalışan AlpNet uygulamalarını kapatıp tekrar deneyin.", "AlpNet Pulse Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }

            // Normal startup for installed app: Robust single-instance mutex with abandoned mutex recovery
            bool isNewInstance = false;
            try
            {
                _mutex = new Mutex(true, "AlpNetPulse_SingleInstance_Mutex", out isNewInstance);
                if (!isNewInstance)
                {
                    AlpNetTelemetryService.Instance.Warn("BAŞLATMA", "Mutex halihazırda mevcut. Olası bir güncelleme veya önceki sürecin kapanması bekleniyor (max 6 sn)...");
                    // Wait up to 6 seconds for previous instance (e.g. during auto-update) to terminate
                    if (_mutex.WaitOne(TimeSpan.FromSeconds(6), false))
                    {
                        isNewInstance = true;
                        AlpNetTelemetryService.Instance.Info("BAŞLATMA", "Önceki süreç kapandı, Mutex sahipliği başarıyla devralındı.");
                    }
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous process exited abruptly or was terminated by updater/OS - we safely inherit the mutex!
                isNewInstance = true;
                AlpNetTelemetryService.Instance.Info("BAŞLATMA", "Terk edilmiş (abandoned) Mutex sahipliği güvenle devralındı.");
            }
            catch (Exception ex)
            {
                AlpNetTelemetryService.Instance.Error("BAŞLATMA", "Mutex oluşturulurken istisna oluştu: " + ex.Message, ex);
                isNewInstance = true; // Fallback so app still launches
            }

            if (!isNewInstance)
            {
                AlpNetTelemetryService.Instance.Warn("BAŞLATMA", "AlpNet Pulse zaten arka planda çalışıyor. Yeni başlatılan örnek sonlandırılıyor.");
                Shutdown();
                return;
            }

            // Ensure app starts automatically on Windows boot, pointing directly to CURRENT executable
            try
            {
                // 1. HKCU Run Registry (Standart başlangıç kaydı)
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        try { key.DeleteValue("AlpNet", false); } catch { }
                        key.SetValue("AlpNetPulse", $"\"{currentExe}\" /autostart");
                    }
                }

                // 2. Windows Görev Zamanlayıcısı (Task Scheduler):
                // Yönetici izinli (requireAdministrator) uygulamalar HKCU Run'dan başlatılırken Windows UAC tarafından sessizce engellenir.
                // Task Scheduler (/rl highest /sc onlogon) ile UAC uyarısı olmadan her açılışta garanti olarak arka planda başlar.
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var taskPsi = new ProcessStartInfo("schtasks.exe", $"/create /tn \"AlpNetPulse\" /tr \"\\\"{currentExe}\\\" /autostart\" /sc onlogon /rl highest /f")
                        {
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var proc = Process.Start(taskPsi);
                        proc?.WaitForExit(3000);
                    }
                    catch { }
                });
            }
            catch { }

            base.OnStartup(e);

            var mainWindow = new MainWindow();
            bool startHidden = false;

            foreach (var arg in e.Args)
            {
                if (arg.ToLower() == "-hidden" || arg.ToLower() == "-minimized" || arg.ToLower() == "/autostart")
                {
                    startHidden = true;
                    break;
                }
            }

            if (startHidden)
            {
                // This ensures it does not flash a black window on screen
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowInTaskbar = false;
                mainWindow.Opacity = 0;
                mainWindow.Show();
                mainWindow.Hide();
                mainWindow.Opacity = 1;
            }
            else
            {
                mainWindow.Show();
            }
        }

        private static int _shutdownOnce = 0;
        public static void SafeGlobalShutdown()
        {
            if (System.Threading.Interlocked.Exchange(ref _shutdownOnce, 1) != 0) return;

            try
            {
                if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
                {
                    mw.PerformShutdownCleanup();
                }

                string[] orphanProcs = { "ffmpeg", "wiresock-client", "wgcf", "dnscrypt-proxy", "translator" };
                foreach (var name in orphanProcs)
                {
                    try
                    {
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            using (p)
                            {
                                try { p.Kill(true); p.WaitForExit(500); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                AlpNetTelemetryService.Instance.Info("SHUTDOWN", $"OnExit çağrıldı! Kod: {e.ApplicationExitCode}");
            }
            catch { }
            SafeGlobalShutdown();
            ReleaseSingleInstanceMutex();
            base.OnExit(e);
        }
    }
}
