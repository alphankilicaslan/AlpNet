using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlpNetPulse
{
    public class ReplaySettings
    {
        [JsonPropertyName("bufferMinutes")]
        public int BufferMinutes { get; set; } = 10;

        [JsonPropertyName("hotkeyDisplay")]
        public string HotkeyDisplay { get; set; } = "F9";

        [JsonPropertyName("hotkeyVirtualKey")]
        public int HotkeyVirtualKey { get; set; } = 0x78; // VK_F9
        
        [JsonPropertyName("hotkeyModifiers")]
        public int HotkeyModifiers { get; set; } = 0; // 1=Ctrl, 2=Shift, 4=Alt

        [JsonPropertyName("fps")]
        public int Fps { get; set; } = 30;

        [JsonPropertyName("quality")]
        public string Quality { get; set; } = "Medium"; // Low, Medium, High

        [JsonPropertyName("recordAudio")]
        public bool RecordAudio { get; set; } = true;

        [JsonPropertyName("recordMic")]
        public bool RecordMic { get; set; } = true;

        [JsonPropertyName("micDeviceName")]
        public string MicDeviceName { get; set; } = "";

        [JsonPropertyName("saveFolder")]
        public string SaveFolder { get; set; } = "";

        [JsonPropertyName("ffmpegPath")]
        public string FfmpegPath { get; set; } = "";

        [JsonPropertyName("autoStartRecording")]
        public bool AutoStartRecording { get; set; } = false;

        private static readonly string _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlpNet");
        private static readonly string _settingsFile = Path.Combine(_settingsDir, "replay_settings.json");

        public ReplaySettings()
        {
            if (string.IsNullOrEmpty(SaveFolder))
            {
                SaveFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AlpNet_Video");
            }
        }

        public static ReplaySettings Load()
        {
            try
            {
                if (File.Exists(_settingsFile))
                {
                    string json = File.ReadAllText(_settingsFile);
                    var settings = JsonSerializer.Deserialize<ReplaySettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch { }
            return new ReplaySettings();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(_settingsDir))
                    Directory.CreateDirectory(_settingsDir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(_settingsFile, json);
            }
            catch { }
        }

        /// <summary>
        /// Gets the bitrate string for FFmpeg based on quality setting
        /// </summary>
        public string GetBitrate()
        {
            return Quality switch
            {
                "Low" => "4M",
                "Medium" => "8M",
                "High" => "15M",
                _ => "8M"
            };
        }

        /// <summary>
        /// Gets the max disk usage estimate in MB
        /// </summary>
        public int GetEstimatedDiskUsageMB()
        {
            int bitrateKbps = Quality switch
            {
                "Low" => 4000,
                "Medium" => 8000,
                "High" => 15000,
                _ => 8000
            };
            // bitrate (kbps) * duration (sec) / 8 / 1024 = MB
            return (int)(bitrateKbps * BufferMinutes * 60L / 8 / 1024);
        }
    }
}
