using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AlpNetPulse
{
    /// <summary>
    /// Thread-safe ve atomik dosya okuma/yazma motoru.
    /// Ani kapanmalarda 0-byte dosya bozulmalarını ve eşzamanlı UI tıklamalarında IOException hatalarını önler.
    /// </summary>
    public static class SafeFileIo
    {
        private static readonly object _fileLock = new object();

        public static T LoadJson<T>(string filePath, T fallback) where T : new()
        {
            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(filePath)) return fallback;

                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string json = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(json)) return fallback;

                    return JsonSerializer.Deserialize<T>(json) ?? fallback;
                }
                catch
                {
                    return fallback;
                }
            }
        }

        public static bool SaveJsonAtomic<T>(string filePath, T data)
        {
            lock (_fileLock)
            {
                string? tempFile = null;
                try
                {
                    string? dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    tempFile = Path.Combine(dir ?? "", $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, options);

                    // 1. Önce geçici dosyaya eksiksiz yaz ve diske flush et
                    using (var fs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(flushToDisk: true);
                    }

                    // 2. Atomik olarak hedef dosyanın üzerine taşı
                    if (File.Exists(filePath))
                    {
                        File.Move(tempFile, filePath, overwrite: true);
                    }
                    else
                    {
                        File.Move(tempFile, filePath);
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    if (tempFile != null && File.Exists(tempFile))
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }
            }
        }
    }
}
