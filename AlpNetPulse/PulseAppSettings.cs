using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlpNetPulse
{
    public class PulseAppSettings
    {
        [JsonPropertyName("autoBypassEnabled")]
        public bool AutoBypassEnabled { get; set; } = true;

        [JsonPropertyName("browserVpnEnabled")]
        public bool BrowserVpnEnabled { get; set; } = false;

        [JsonPropertyName("bypassModeIndex")]
        public int BypassModeIndex { get; set; } = 0;

        [JsonPropertyName("autoDnsMode")]
        public string AutoDnsMode { get; set; } = "dhcp";

        [JsonPropertyName("autoDiscordStart")]
        public bool AutoDiscordStart { get; set; } = true;

        [JsonPropertyName("autoDiscordStop")]
        public bool AutoDiscordStop { get; set; } = true;

        [JsonPropertyName("voiceSourceLangIndex")]
        public int VoiceSourceLangIndex { get; set; } = 0;

        [JsonPropertyName("voiceTargetLangIndex")]
        public int VoiceTargetLangIndex { get; set; } = 1;

        [JsonPropertyName("voiceOutIndex")]
        public int VoiceOutIndex { get; set; } = 0;

        [JsonPropertyName("reverseSourceIndex")]
        public int ReverseSourceIndex { get; set; } = 0;

        [JsonPropertyName("reverseOutputIndex")]
        public int ReverseOutputIndex { get; set; } = 0;

        [JsonPropertyName("reverseTranslationActive")]
        public bool ReverseTranslationActive { get; set; } = false;

        [JsonPropertyName("reverseTTSActive")]
        public bool ReverseTTSActive { get; set; } = false;

        public static PulseAppSettings Load()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet", "pulse_settings.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<PulseAppSettings>(json) ?? new PulseAppSettings();
                }
            }
            catch { }
            return new PulseAppSettings();
        }

        public void Save()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlpNet");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, "pulse_settings.json");
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }
}
