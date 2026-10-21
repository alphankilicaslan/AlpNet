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

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                AlpNetTelemetryService.Instance.HookGlobalExceptions();
                AlpNetTelemetryService.Instance.Info("BAŞLATMA", $"Uygulama açılıyor... Argümanlar: {string.Join(" ", e.Args)}");
            }
            catch { }

            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string exeName = Path.GetFileName(currentExe).ToLower();
            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet");
            string installExe = Path.Combine(installDir, "AlpNet.exe");

            // Check if this instance is running as the installer
            bool isInstaller = !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(installExe), StringComparison.OrdinalIgnoreCase) && 
                               (exeName.Contains("setup") || exeName.Contains("kur") || exeName.Contains("yükle"));

            if (isInstaller)
            {
                try
                {

                    // 1. Kill any running instance of AlpNet.exe to avoid file lock
                    foreach (var proc in Process.GetProcessesByName("AlpNet"))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(2000);
                        }
                        catch { }
                    }
                    foreach (var proc in Process.GetProcessesByName("AlpNetPulse"))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(2000);
                        }
                        catch { }
                    }

                    // Give Windows some time to release file locks
                    Thread.Sleep(500);

                    // 2. Create installation directory
                    if (!Directory.Exists(installDir))
                    {
                        Directory.CreateDirectory(installDir);
                    }

                    // 3. Copy executable
                    int retries = 6;
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
                    System.Windows.MessageBox.Show("Kurulum hatası: " + ex.Message + "\nLütfen çalışan AlpNet uygulamalarını kapatıp tekrar deneyin.", "AlpNet Pulse Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }

            // Normal startup for installed app
            bool isNewInstance;
            _mutex = new Mutex(true, "AlpNetPulse_SingleInstance_Mutex", out isNewInstance);
            if (!isNewInstance)
            {
                // App is already running, exit this new instance
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

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _mutex = null;
            }
            catch { }
            base.OnExit(e);
        }
    }
}
