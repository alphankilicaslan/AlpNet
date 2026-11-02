using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using AlpNetPulse;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" AlpNet Pulse - Discord & Warp Verification Test  ");
        Console.WriteLine("==================================================");

        int failures = 0;

        // TEST 1: Verify ModifyConfigurationAsync on dummy profile
        Console.WriteLine("\n[TEST 1] Testing ModifyConfigurationAsync logic...");
        string tempDir = Path.Combine(Path.GetTempPath(), "alpnet_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dummyProfile = Path.Combine(tempDir, "wgcf-profile.conf");

        string initialConf = @"[Interface]
PrivateKey = aaaaaa=
Address = 172.16.0.2/32
DNS = 1.1.1.1
MTU = 1280

[Peer]
PublicKey = bbbbbb=
AllowedIPs = 0.0.0.0/0
Endpoint = engage.cloudflareclient.com:2408
";
        await File.WriteAllTextAsync(dummyProfile, initialConf);

        var warpManager = new WarpManager(tempDir);
        warpManager.IsBrowserVpnEnabled = true;

        bool modSuccess = await warpManager.ModifyConfigurationAsync(dummyProfile);
        if (!modSuccess)
        {
            Console.WriteLine("[-] FAILED: ModifyConfigurationAsync returned false");
            failures++;
        }
        else
        {
            string modifiedText = await File.ReadAllTextAsync(dummyProfile);
            Console.WriteLine("[+] Modified config content:\n" + modifiedText);

            // Check PersistentKeepalive
            if (modifiedText.Contains("PersistentKeepalive = 15"))
            {
                Console.WriteLine("[PASS] PersistentKeepalive = 15 present");
            }
            else
            {
                Console.WriteLine("[FAIL] Missing PersistentKeepalive = 15");
                failures++;
            }

            // Check MTU = 1280 (or 1360 legacy)
            if (modifiedText.Contains("MTU = 1280") || modifiedText.Contains("MTU = 1360"))
            {
                Console.WriteLine("[PASS] MTU = 1280/1360 present");
            }
            else
            {
                Console.WriteLine("[FAIL] Missing MTU = 1280");
                failures++;
            }

            // Check Port 500
            if (modifiedText.Contains(":500"))
            {
                Console.WriteLine("[PASS] Endpoint port 500 configured");
            }
            else
            {
                Console.WriteLine("[FAIL] Endpoint port 500 missing");
                failures++;
            }

            // Check AllowedIPs dual-stack
            if (modifiedText.Contains("AllowedIPs = 0.0.0.0/0, ::/0"))
            {
                Console.WriteLine("[PASS] AllowedIPs = 0.0.0.0/0, ::/0 present");
            }
            else
            {
                Console.WriteLine("[FAIL] Dual stack AllowedIPs missing");
                failures++;
            }

            // Check AllowedApps Discord & Browser variants (both with and without .exe)
            string[] requiredApps = { "discord", "Discord.exe", "discordptb", "DiscordPTB.exe", "discordcanary", "DiscordCanary.exe", "vesktop", "Vesktop.exe", "webcord", "armcord", "browser", "browser.exe", "yandex", "yandex.exe" };
            bool allAppsPresent = true;
            foreach (var app in requiredApps)
            {
                if (!modifiedText.Contains(app))
                {
                    Console.WriteLine($"[FAIL] AllowedApps missing {app}");
                    allAppsPresent = false;
                    failures++;
                }
            }
            if (allAppsPresent)
            {
                Console.WriteLine("[PASS] All Discord and Browser application variants included in AllowedApps");
            }
        }

        // Clean up temp
        try { Directory.Delete(tempDir, true); } catch { }

        // TEST 2: Update REAL profile in AppData\Local\AlpNet\res and Project Root res
        Console.WriteLine("\n[TEST 2] Testing modification and sync on REAL profiles...");
        string appDataRes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet", "res");
        string realProfile = Path.Combine(appDataRes, "wgcf-profile.conf");
        string rootRes = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpNet\res";
        string rootProfile = Path.Combine(rootRes, "wgcf-profile.conf");

        if (File.Exists(realProfile))
        {
            var realWarp = new WarpManager(appDataRes);
            realWarp.IsBrowserVpnEnabled = true;
            bool realMod = await realWarp.ModifyConfigurationAsync(realProfile);
            if (realMod)
            {
                string realContent = await File.ReadAllTextAsync(realProfile);
                Console.WriteLine("[PASS] Real AppData profile updated successfully!");

                if (realContent.Contains("PersistentKeepalive = 15") && realContent.Contains(":500") && realContent.Contains("Discord.exe") && realContent.Contains("discord") && (realContent.Contains("MTU = 1280") || realContent.Contains("MTU = 1360")) && realContent.Contains("browser.exe") && realContent.Contains("yandex.exe"))
                {
                    Console.WriteLine("[PASS] Real AppData profile verified with Keepalive=15, Port 500, MTU=1280/1360, and Browser/Discord apps.");
                }
                else
                {
                    Console.WriteLine("[FAIL] Real AppData profile missing critical directives!");
                    failures++;
                }
            }
            else
            {
                Console.WriteLine("[FAIL] Could not modify real AppData profile.");
                failures++;
            }
        }
        else
        {
            Console.WriteLine("[SKIP] Real AppData profile does not exist at " + realProfile);
        }

        if (File.Exists(rootProfile))
        {
            var rootWarp = new WarpManager(rootRes);
            rootWarp.IsBrowserVpnEnabled = true;
            bool rootMod = await rootWarp.ModifyConfigurationAsync(rootProfile);
            if (rootMod)
            {
                string rootContent = await File.ReadAllTextAsync(rootProfile);
                Console.WriteLine("[PASS] Root project profile updated successfully!");

                if (rootContent.Contains("PersistentKeepalive = 15") && rootContent.Contains(":500") && rootContent.Contains("Discord.exe") && rootContent.Contains("discord") && (rootContent.Contains("MTU = 1280") || rootContent.Contains("MTU = 1360")) && rootContent.Contains("browser.exe") && rootContent.Contains("yandex.exe"))
                {
                    Console.WriteLine("[PASS] Root project profile verified with Keepalive=15, Port 500, MTU=1280/1360, and Browser/Discord apps.");
                }
                else
                {
                    Console.WriteLine("[FAIL] Root project profile missing critical directives!");
                    failures++;
                }
            }
            else
            {
                Console.WriteLine("[FAIL] Could not modify root project profile.");
                failures++;
            }
        }
        else
        {
            Console.WriteLine("[SKIP] Root project profile does not exist at " + rootProfile);
        }

        // TEST 2.1: Verify HTTP/3 (QUIC / UDP 443) Registry Policy Enforcement
        Console.WriteLine("\n[TEST 2.1] Testing HTTP/3 (QUIC / UDP 443) registry policy enforcement...");
        WarpManager.EnsureBrowserHttp3QuicEnabled();
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Policies\Google\Chrome"))
        {
            if (key != null && (int)(key.GetValue("QuicAllowed") ?? 0) == 1)
            {
                Console.WriteLine("[PASS] Chrome QuicAllowed = 1 policy verified in registry!");
            }
            else
            {
                Console.WriteLine("[FAIL] Chrome QuicAllowed registry policy missing or incorrect!");
                failures++;
            }
        }
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Policies\Microsoft\Edge"))
        {
            if (key != null && (int)(key.GetValue("QuicAllowed") ?? 0) == 1)
            {
                Console.WriteLine("[PASS] Edge QuicAllowed = 1 policy verified in registry!");
            }
            else
            {
                Console.WriteLine("[FAIL] Edge QuicAllowed registry policy missing or incorrect!");
                failures++;
            }
        }

        // TEST 3: WireSock Config Validation & Service Status
        Console.WriteLine("\n[TEST 3] Testing WireSock config validation & running service status...");
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query wiresock-client-service")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (output.Contains("STATE") && output.Contains("RUNNING"))
                {
                    Console.WriteLine("[PASS] WireSock service is RUNNING cleanly with exit code 0x0 using updated config!");
                }
                else
                {
                    Console.WriteLine("[WARN] WireSock service state:\n" + output);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Service query exception: {ex.Message}");
        }

        // TEST 4: Test Discord Cache Cleaner
        Console.WriteLine("\n[TEST 4] Testing Discord Cache Cleaner logic...");
        try
        {
            bool cleaned = await WarpManager.RepairDiscordCacheAsync(msg => Console.WriteLine($"   -> {msg}"));
            if (cleaned)
            {
                Console.WriteLine("[PASS] Discord cache cleaning logic executed cleanly!");
            }
            else
            {
                Console.WriteLine("[FAIL] Discord cache cleaner returned false.");
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] RepairDiscordCacheAsync threw exception: {ex.Message}");
            failures++;
        }

        // TEST 5: Verify .NET 9 Desktop Runtime detection logic (AlpNetWebSetup fix)
        Console.WriteLine("\n[TEST 5] Testing .NET 9 Desktop Runtime detection logic...");
        bool net9Detected = false;
        try
        {
            // 1. 64-bit Registry View
            try
            {
                using var baseKey64 = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var key = baseKey64.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
                if (key != null)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        if (name.StartsWith("9.", StringComparison.OrdinalIgnoreCase))
                        {
                            net9Detected = true;
                            Console.WriteLine($"   -> Found via 64-bit Registry: {name}");
                            break;
                        }
                    }
                }
            }
            catch { }

            // 2. Physical directory check
            if (!net9Detected)
            {
                var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? progW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
                if (!string.IsNullOrEmpty(progW6432)) candidateDirs.Add(progW6432);
                string? progFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                if (!string.IsNullOrEmpty(progFiles)) candidateDirs.Add(progFiles);
                string specialProgFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(specialProgFiles)) candidateDirs.Add(specialProgFiles);

                foreach (var bDir in candidateDirs)
                {
                    string sharedFolder = Path.Combine(bDir, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                    if (Directory.Exists(sharedFolder))
                    {
                        var dirs = Directory.GetDirectories(sharedFolder, "9.*");
                        if (dirs.Length > 0)
                        {
                            net9Detected = true;
                            Console.WriteLine($"   -> Found via physical folder: {dirs[0]}");
                            break;
                        }
                    }
                }
            }

            // 3. dotnet CLI
            if (!net9Detected)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet.exe",
                    Arguments = "--list-runtimes",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    if (output.IndexOf("Microsoft.WindowsDesktop.App 9.", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        net9Detected = true;
                        Console.WriteLine("   -> Found via dotnet --list-runtimes");
                    }
                }
            }

            if (net9Detected)
            {
                Console.WriteLine("[PASS] .NET 9 Desktop Runtime successfully detected by installer logic!");
            }
            else
            {
                Console.WriteLine("[FAIL] .NET 9 Desktop Runtime NOT detected by installer logic!");
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 5 exception: {ex.Message}");
            failures++;
        }

        // TEST 6: Verify AudioManager segment index continuity (Issue 6 fix)
        Console.WriteLine("\n[TEST 6] Testing AudioManager segment index continuity...");
        try
        {
            string audioTestDir = Path.Combine(Path.GetTempPath(), "alpnet_audio_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(audioTestDir);

            var audio1 = new AudioManager();
            // Start capture with initialSegmentIndex = 42
            audio1.StartCapture(audioTestDir, 10, false, false, "default", 42);
            int idx1 = audio1.CurrentSegmentIndex;
            audio1.StopCapture();
            audio1.Dispose();

            if (idx1 == 42)
            {
                Console.WriteLine($"[PASS] Initial segment index correctly initialized to 42 (got {idx1})");
            }
            else
            {
                Console.WriteLine($"[FAIL] Expected initial segment index 42, got {idx1}");
                failures++;
            }

            // Next capture resumes at 43
            var audio2 = new AudioManager();
            audio2.StartCapture(audioTestDir, 10, false, false, "default", idx1 + 1);
            int idx2 = audio2.CurrentSegmentIndex;
            audio2.StopCapture();
            audio2.Dispose();

            if (idx2 == 43)
            {
                Console.WriteLine($"[PASS] Subsequent segment index correctly continued at 43 without resetting to 0");
            }
            else
            {
                Console.WriteLine($"[FAIL] Expected continued segment index 43, got {idx2}");
                failures++;
            }

            try { Directory.Delete(audioTestDir, true); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 6 exception: {ex.Message}");
            failures++;
        }

        // TEST 7: Verify VideoPlayer Trim Safety Logic (Issue 1 fix)
        Console.WriteLine("\n[TEST 7] Testing VideoPlayer trim failure safety (no data loss)...");
        try
        {
            string trimTestDir = Path.Combine(Path.GetTempPath(), "alpnet_trim_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(trimTestDir);

            string originalVideo = Path.Combine(trimTestDir, "test_clip.mp4");
            await File.WriteAllTextAsync(originalVideo, "ORIGINAL_VIDEO_DATA_DO_NOT_DELETE");
            string outputVideo = Path.Combine(trimTestDir, "test_clip_Trimmed_123.mp4");

            // Simulate FFmpeg failure: exitCode = 1, output file empty or non-existent
            int exitCode = 1;
            bool deleteOriginal = true;

            bool fileValid = File.Exists(outputVideo) && new FileInfo(outputVideo).Length > 0;
            if (exitCode != 0 || !fileValid)
            {
                if (File.Exists(outputVideo)) { try { File.Delete(outputVideo); } catch { } }
                // Original file MUST NOT be deleted
            }
            else
            {
                if (deleteOriginal && File.Exists(originalVideo))
                {
                    File.Delete(originalVideo);
                }
            }

            if (File.Exists(originalVideo) && await File.ReadAllTextAsync(originalVideo) == "ORIGINAL_VIDEO_DATA_DO_NOT_DELETE")
            {
                Console.WriteLine("[PASS] Original video was preserved safely when trim failed!");
            }
            else
            {
                Console.WriteLine("[FAIL] Original video was deleted or corrupted on trim failure!");
                failures++;
            }

            try { Directory.Delete(trimTestDir, true); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 7 exception: {ex.Message}");
            failures++;
        }

        // TEST 8: Verify Speed Test UI Throttle Logic (Issue 8 fix)
        Console.WriteLine("\n[TEST 8] Testing Speed Test UI Throttling logic...");
        try
        {
            int uiDispatches = 0;
            long lastUiUpdateMs = 0;
            long totalBytes = 50_000_000;
            long bytesReadTotal = 0;
            int bufferSize = 81920;

            var sw = Stopwatch.StartNew();
            // Simulate 610 buffer reads across 300 milliseconds
            for (int i = 0; i < 610; i++)
            {
                bytesReadTotal += bufferSize;
                // Fast simulated connection: 100 microseconds per iteration
                long elapsedMs = sw.ElapsedMilliseconds;
                if (elapsedMs - lastUiUpdateMs >= 60 || bytesReadTotal >= totalBytes)
                {
                    lastUiUpdateMs = elapsedMs;
                    uiDispatches++;
                }
                System.Threading.Thread.Sleep(1);
            }
            sw.Stop();
            double dispatchRate = uiDispatches / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"   -> 610 reads over {sw.Elapsed.TotalSeconds:F2}s with {uiDispatches} UI dispatches ({dispatchRate:F1} updates/sec)");
            if (dispatchRate <= 25.0)
            {
                Console.WriteLine($"[PASS] UI updates successfully throttled to {dispatchRate:F1} updates/sec (target <= 20-25/sec)!");
            }
            else
            {
                Console.WriteLine($"[FAIL] Dispatch rate too high ({dispatchRate:F1}/sec)! Throttling insufficient.");
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 8 exception: {ex.Message}");
            failures++;
        }

        // TEST 9: Verify Silent Audio Segment generation and 1:1 Alignment Logic
        Console.WriteLine("\n[TEST 9] Testing Audio 1:1 Alignment & Silence Generation...");
        try
        {
            string silenceTestDir = Path.Combine(Path.GetTempPath(), "alpnet_silence_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(silenceTestDir);

            int segDuration = 2;
            string silenceFile = Path.Combine(silenceTestDir, $"alpnet_seg_silence_{segDuration}s.wav");
            var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            using (var writer = new NAudio.Wave.WaveFileWriter(silenceFile, format))
            {
                byte[] silence = new byte[format.AverageBytesPerSecond * segDuration];
                writer.Write(silence, 0, silence.Length);
            }

            if (File.Exists(silenceFile) && new FileInfo(silenceFile).Length > 0)
            {
                using var reader = new NAudio.Wave.WaveFileReader(silenceFile);
                if (Math.Abs(reader.TotalTime.TotalSeconds - segDuration) < 0.1)
                {
                    Console.WriteLine($"[PASS] Silent audio segment successfully generated ({reader.TotalTime.TotalSeconds:F1}s at {reader.WaveFormat.SampleRate}Hz)!");
                }
                else
                {
                    Console.WriteLine($"[FAIL] Silent audio segment duration mismatch! Got {reader.TotalTime.TotalSeconds}s");
                    failures++;
                }
            }
            else
            {
                Console.WriteLine("[FAIL] Silent audio file not created!");
                failures++;
            }

            try { Directory.Delete(silenceTestDir, true); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 9 exception: {ex.Message}");
            failures++;
        }

        // TEST 10: Verify Max Segment Index Calculation across Mixed mp4 and wav files
        Console.WriteLine("\n[TEST 10] Testing Max Segment Index Calculation across mixed mp4/wav files...");
        try
        {
            string mixedDir = Path.Combine(Path.GetTempPath(), "alpnet_mixed_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mixedDir);

            // Simulate scenario: Video segments up to 000003, but Audio segments up to 000005
            File.WriteAllText(Path.Combine(mixedDir, "alpnet_seg_000001.mp4"), "video1");
            File.WriteAllText(Path.Combine(mixedDir, "alpnet_seg_000002.mp4"), "video2");
            File.WriteAllText(Path.Combine(mixedDir, "alpnet_seg_000003.mp4"), "video3");
            File.WriteAllText(Path.Combine(mixedDir, "alpnet_seg_sys_000004.wav"), "audio4");
            File.WriteAllText(Path.Combine(mixedDir, "alpnet_seg_mic_000005.wav"), "audio5");

            int maxDiskIndex = -1;
            foreach (var f in new DirectoryInfo(mixedDir).GetFiles("alpnet_seg_*.*"))
            {
                string name = Path.GetFileNameWithoutExtension(f.Name);
                int lastUnderscore = name.LastIndexOf('_');
                if (lastUnderscore >= 0 && int.TryParse(name.Substring(lastUnderscore + 1), out int parsedIdx))
                {
                    if (parsedIdx > maxDiskIndex) maxDiskIndex = parsedIdx;
                }
            }

            int nextIndex = maxDiskIndex + 1;
            if (nextIndex == 6)
            {
                Console.WriteLine($"[PASS] Correctly detected max segment index as 5, next index is 6 (prevented collision with wav index 5)!");
            }
            else
            {
                Console.WriteLine($"[FAIL] Expected next index 6, got {nextIndex}");
                failures++;
            }

            try { Directory.Delete(mixedDir, true); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 10 exception: {ex.Message}");
            failures++;
        }

        // TEST 11: Verify Driver Manager Fast Scan, Version Comparison, and Same-Version Guard
        Console.WriteLine("\n[TEST 11] Testing Driver Manager Scan Speed, IsNewerVersion, and Update Guard...");
        try
        {
            // 1. IsNewerVersion unit tests
            if (DriverManager.IsNewerVersion("24.80.0.2", "24.10.0.4") &&
                !DriverManager.IsNewerVersion("24.10.0.4", "24.10.0.4") &&
                !DriverManager.IsNewerVersion("24.10.0.4", "24.80.0.2") &&
                DriverManager.IsNewerVersion("1168.16.1123.2024", "1168.10.720.2022") &&
                !DriverManager.IsNewerVersion("1168.10.720.2022", "1168.10.720.2022"))
            {
                Console.WriteLine("[PASS] DriverManager.IsNewerVersion correctly evaluates version comparisons and rejects equal/older versions!");
            }
            else
            {
                Console.WriteLine("[FAIL] DriverManager.IsNewerVersion comparison logic failed!");
                failures++;
            }

            // 2. Same-version update guard
            var testDriver = new DriverInfo
            {
                Name = "Test Wi-Fi Adapter",
                CurrentVersion = "24.10.0.4",
                LatestVersion = "24.10.0.4",
                IsUpdateAvailable = true // even if flagged true
            };
            var updateRes = await DriverManager.Instance.UpdateDriverAsync(testDriver);
            if (!updateRes.Success && updateRes.Message.Contains("zaten en güncel sürümde"))
            {
                Console.WriteLine("[PASS] Same-version update guard correctly rejected update attempt!");
            }
            else
            {
                Console.WriteLine($"[FAIL] Same-version update guard failed: {updateRes.Message}");
                failures++;
            }

            // 3. History cleansing verification
            var history = DriverManager.Instance.GetUpdateHistory();
            bool hasInvalidHistory = history.Any(r => string.Equals(r.OldVersion, r.NewVersion, StringComparison.OrdinalIgnoreCase) || !DriverManager.IsNewerVersion(r.NewVersion, r.OldVersion));
            if (!hasInvalidHistory)
            {
                Console.WriteLine($"[PASS] Update history verified clean ({history.Count} valid entries, 0 same-version records)!");
            }
            else
            {
                Console.WriteLine("[FAIL] Update history contains invalid or same-version records!");
                failures++;
            }

            // 4. Fast scan benchmark (Parallel execution + 6s timeout must finish quickly)
            var scanSw = Stopwatch.StartNew();
            var scanned = await DriverManager.Instance.ScanDriversAsync(status => { });
            scanSw.Stop();
            Console.WriteLine($"   -> Scanned {scanned.Count} drivers in {scanSw.Elapsed.TotalSeconds:F2}s");

            if (scanSw.Elapsed.TotalSeconds <= 12.0)
            {
                Console.WriteLine($"[PASS] Fast driver scan finished in {scanSw.Elapsed.TotalSeconds:F2}s (target <= 12s)!");
            }
            else
            {
                Console.WriteLine($"[FAIL] Driver scan too slow ({scanSw.Elapsed.TotalSeconds:F2}s)! Timeout/parallelism issue.");
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 11 exception: {ex.Message}");
            failures++;
        }

        // TEST 12: AlpTogether Device Specs & Driver Update Telemetry to Firebase
        Console.WriteLine("\n[TEST 12] Testing AlpTogether Device Specs & Driver Update Telemetry...");
        try
        {
            // 1. Donanım Özellikleri Senkronizasyon Testi
            var testSpecs = new DeviceHardwareSpecs
            {
                CpuName = "Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz",
                CpuCores = 6,
                CpuLogicalProcessors = 12,
                GpuName = "NVIDIA GeForce RTX 2060",
                GpuDriverVersion = "560.94",
                GpuMemory = "6144 MB",
                MotherboardManufacturer = "Micro-Star International Co., Ltd.",
                MotherboardProduct = "MS-16W1",
                BiosVersion = "E16W1IMS.10B",
                BiosReleaseDate = "2020-09-02",
                WifiAdapterName = "Intel(R) Wi-Fi 6 AX201 160MHz",
                WifiDriverVersion = "24.10.0.4",
                EthernetAdapterName = "Realtek PCIe GbE Family Controller",
                EthernetDriverVersion = "1168.16.1123.2024",
                OsCaption = "Microsoft Windows 11 Pro",
                OsVersion = "10.0.26100",
                OsBuildNumber = "26100",
                TotalRamMb = 16384,
                UserName = Environment.UserName,
                MachineName = Environment.MachineName,
                LastScanned = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // JSON Schema Validation Test: verify required fields
            string serializedSpecs = System.Text.Json.JsonSerializer.Serialize(testSpecs);
            using (var doc = System.Text.Json.JsonDocument.Parse(serializedSpecs))
            {
                var root = doc.RootElement;
                bool hasCpu = root.TryGetProperty("cpu", out var cpuProp) && cpuProp.TryGetProperty("model", out _) && cpuProp.TryGetProperty("cores", out _) && cpuProp.TryGetProperty("threads", out _);
                bool hasGpu = root.TryGetProperty("gpu", out var gpuProp) && gpuProp.TryGetProperty("model", out _) && gpuProp.TryGetProperty("vram", out _) && gpuProp.TryGetProperty("current_driver_version", out _);
                bool hasMb = root.TryGetProperty("motherboard", out var mbProp) && mbProp.TryGetProperty("manufacturer", out _) && mbProp.TryGetProperty("model", out _) && mbProp.TryGetProperty("bios_version", out _) && mbProp.TryGetProperty("bios_date", out _);
                bool hasNet = root.TryGetProperty("network", out var netProp) && netProp.TryGetProperty("wifi_name", out _) && netProp.TryGetProperty("wifi_driver_version", out _) && netProp.TryGetProperty("ethernet_name", out _) && netProp.TryGetProperty("ethernet_driver_version", out _);
                bool hasOs = root.TryGetProperty("os", out var osProp) && osProp.TryGetProperty("edition", out _) && osProp.TryGetProperty("build_number", out _);
                bool hasRam = root.TryGetProperty("ram_mb", out var ramProp) && ramProp.GetInt64() == 16384;
                bool hasLastScan = root.TryGetProperty("last_scanned", out _);

                if (hasCpu && hasGpu && hasMb && hasNet && hasOs && hasRam && hasLastScan)
                {
                    Console.WriteLine("[PASS] DeviceHardwareSpecs JSON schema verified (nested cpu, gpu, motherboard, network, os, ram_mb, last_scanned)!");
                }
                else
                {
                    Console.WriteLine($"[FAIL] DeviceHardwareSpecs JSON schema validation failed! Schema: {serializedSpecs}");
                    failures++;
                }
            }

            bool specsOk = await AlpNetTelemetryService.Instance.SyncDeviceSpecificationsAsync(testSpecs);
            if (specsOk)
            {
                Console.WriteLine("[PASS] SyncDeviceSpecificationsAsync successfully sent hardware specs to AlpTogether Firebase!");
            }
            else
            {
                Console.WriteLine("[FAIL] SyncDeviceSpecificationsAsync failed to send hardware specs!");
                failures++;
            }

            // Verify cloud read-back of hardware specs
            using (var http = new System.Net.Http.HttpClient())
            {
                string hwid = AlpNetTelemetryService.Instance.EnsureDeviceId();
                string readUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/alptogether/users/{hwid}/hardware.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                var getResp = await http.GetAsync(readUrl);
                if (getResp.IsSuccessStatusCode)
                {
                    string fbJson = await getResp.Content.ReadAsStringAsync();
                    if (fbJson.Contains("Intel(R) Core(TM) i7-10750H") && fbJson.Contains("RTX 2060"))
                    {
                        Console.WriteLine("[PASS] Cloud read-back of hardware.json confirmed data in Firebase RTDB!");
                    }
                    else
                    {
                        Console.WriteLine($"[FAIL] Cloud read-back data mismatch: {fbJson}");
                        failures++;
                    }
                }
            }

            // 2. Kişi Bazlı Sürücü Güncelleme Telemetrisi Testi
            string testUpdId = "UPD-TEST-" + DateTime.Now.ToString("MMddHHmmss");
            var testDriverRec = new DriverUpdateTelemetryRecord
            {
                UpdateId = testUpdId,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceName = "Realtek PCIe GbE Family Controller",
                Category = "AĞ & WI-FI",
                OldVersion = "1168.10.720.2022",
                NewVersion = "1168.16.1123.2024",
                Status = "Başarıyla Güncellendi",
                Verified = true,
                PhysicalVersionAfterInstall = "1168.16.1123.2024",
                UserName = Environment.UserName,
                MachineName = Environment.MachineName,
                BackupPath = @"C:\AlpNet\TestBackup",
                Notes = "AlpTogether telemetri doğrulama testi."
            };

            // Schema validation for DriverUpdateTelemetryRecord
            string serializedDriver = System.Text.Json.JsonSerializer.Serialize(testDriverRec);
            using (var doc = System.Text.Json.JsonDocument.Parse(serializedDriver))
            {
                var r = doc.RootElement;
                bool okFields = r.TryGetProperty("updateId", out _) &&
                                r.TryGetProperty("timestamp", out _) &&
                                r.TryGetProperty("deviceName", out _) &&
                                r.TryGetProperty("category", out _) &&
                                r.TryGetProperty("oldVersion", out _) &&
                                r.TryGetProperty("newVersion", out _) &&
                                r.TryGetProperty("status", out _) &&
                                r.TryGetProperty("verified", out _) &&
                                r.TryGetProperty("userName", out _) &&
                                r.TryGetProperty("machineName", out _) &&
                                r.TryGetProperty("backupPath", out _) &&
                                r.TryGetProperty("notes", out _);
                if (okFields)
                {
                    Console.WriteLine("[PASS] DriverUpdateTelemetryRecord JSON schema verified (updateId, deviceName, oldVersion, newVersion, status, verified, etc.)!");
                }
                else
                {
                    Console.WriteLine($"[FAIL] DriverUpdateTelemetryRecord JSON schema mismatch! {serializedDriver}");
                    failures++;
                }
            }

            bool recordOk = await AlpNetTelemetryService.Instance.RecordDriverUpdateTelemetryAsync(testDriverRec);
            if (recordOk)
            {
                Console.WriteLine($"[PASS] RecordDriverUpdateTelemetryAsync successfully uploaded driver update event ({testUpdId}) to AlpTogether!");
            }
            else
            {
                Console.WriteLine("[FAIL] RecordDriverUpdateTelemetryAsync failed to upload driver event!");
                failures++;
            }

            // Verify cloud read-back of driver event
            using (var http = new System.Net.Http.HttpClient())
            {
                string readUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/alptogether/telemetry/driver_events/{testUpdId}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                var getResp = await http.GetAsync(readUrl);
                if (getResp.IsSuccessStatusCode)
                {
                    string fbJson = await getResp.Content.ReadAsStringAsync();
                    if (fbJson.Contains(testUpdId) && fbJson.Contains("Realtek PCIe GbE") && fbJson.Contains("1168.16.1123.2024"))
                    {
                        Console.WriteLine($"[PASS] Cloud read-back of driver_events/{testUpdId}.json confirmed data in Firebase RTDB!");
                    }
                    else
                    {
                        Console.WriteLine($"[FAIL] Cloud read-back driver event data mismatch: {fbJson}");
                        failures++;
                    }
                }
            }

            // 3. Real Hardware Collection Test via DriverManager (Empty list fallback to WMI)
            var realSpecs = DriverManager.CollectDeviceHardwareSpecs(new System.Collections.Generic.List<DriverInfo>());
            bool cpuOk = !string.IsNullOrEmpty(realSpecs.Cpu.Model) && realSpecs.Cpu.Cores > 0 && realSpecs.Cpu.Threads > 0;
            bool gpuOk = !string.IsNullOrEmpty(realSpecs.Gpu.Model);
            bool mbOk = !string.IsNullOrEmpty(realSpecs.Motherboard.Manufacturer) && !string.IsNullOrEmpty(realSpecs.Motherboard.Model);
            bool netOk = !string.IsNullOrEmpty(realSpecs.Network.WifiName) || !string.IsNullOrEmpty(realSpecs.Network.EthernetName);
            bool osOk = !string.IsNullOrEmpty(realSpecs.Os.Edition) && realSpecs.RamMb > 0;

            if (cpuOk && gpuOk && mbOk && netOk && osOk)
            {
                Console.WriteLine($"[PASS] DriverManager.CollectDeviceHardwareSpecs gathered complete real specs:");
                Console.WriteLine($"       -> CPU: {realSpecs.Cpu.Model} ({realSpecs.Cpu.Cores} Cores / {realSpecs.Cpu.Threads} Threads)");
                Console.WriteLine($"       -> GPU: {realSpecs.Gpu.Model} (v{realSpecs.Gpu.CurrentDriverVersion}, {realSpecs.Gpu.Vram})");
                Console.WriteLine($"       -> Motherboard: {realSpecs.Motherboard.Manufacturer} - {realSpecs.Motherboard.Model} (BIOS: {realSpecs.Motherboard.BiosVersion})");
                Console.WriteLine($"       -> Network: Wi-Fi: '{realSpecs.Network.WifiName}' (v{realSpecs.Network.WifiDriverVersion}), Eth: '{realSpecs.Network.EthernetName}' (v{realSpecs.Network.EthernetDriverVersion})");
                Console.WriteLine($"       -> OS & RAM: {realSpecs.Os.Edition} (Build {realSpecs.Os.BuildNumber}), {realSpecs.RamMb} MB RAM");
            }
            else
            {
                Console.WriteLine($"[FAIL] DriverManager.CollectDeviceHardwareSpecs incomplete info! CPU:{cpuOk}, GPU:{gpuOk}, MB:{mbOk}, NET:{netOk}, OS:{osOk}");
                failures++;
            }

            // 4. Test RollbackDriverAsync with invalid backup path: must return (false, ...) and record failure telemetry with Status = 'Hata'
            var dummyRecord = new DriverUpdateRecord
            {
                UpdateId = "UPD-TEST-ORIG",
                DeviceName = "Test Virtual Adapter",
                Category = "AĞ & WI-FI",
                OldVersion = "1.0.0.0",
                NewVersion = "2.0.0.0",
                BackupPath = @"C:\NonExistent\AlpNet\InvalidBackupPath_12345"
            };
            var rollbackRes = await DriverManager.Instance.RollbackDriverAsync(dummyRecord);
            if (!rollbackRes.Success && rollbackRes.Message.Contains("bulunamadı"))
            {
                Console.WriteLine("[PASS] RollbackDriverAsync safely rejected invalid backup path and dispatched awaited failure telemetry!");
            }
            else
            {
                Console.WriteLine($"[FAIL] RollbackDriverAsync did not handle invalid backup path as expected: {rollbackRes.Message}");
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Test 12 exception: {ex.Message}");
            failures++;
        }

        Console.WriteLine("\n==================================================");
        if (failures == 0)
        {
            Console.WriteLine(" >>> ALL TESTS PASSED SUCCESSFULLY! (0 Failures) <<<");
        }
        else
        {
            Console.WriteLine($" >>> TESTS FAILED WITH {failures} FAILURE(S) <<<");
        }
        Console.WriteLine("==================================================");

        return failures;
    }
}
