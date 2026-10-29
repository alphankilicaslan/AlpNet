namespace AlpNetPulse
{
    /// <summary>
    /// Modül ve özellik açma / kapama anahtarları (Feature Flags).
    /// İleride bu özellikleri tekrar aktif etmek istediğinizde buradaki ilgili bayrağı 'true' yapmanız yeterlidir.
    /// </summary>
    public static class FeatureFlags
    {
        /// <summary>
        /// Wi-Fi Analizatörü (Canlı Radar, Kanal & Sinyal Analizi)
        /// false: UI menüsünden gizlenir ve arka plan Wi-Fi taramaları çalışmaz.
        /// true: Tekrar aktif hale gelir.
        /// </summary>
        public static bool EnableWifiAnalyzer { get; set; } = false;

        /// <summary>
        /// Oyun Kaydı (Instant Replay, FFmpeg arabelleği ve klip kaydetme)
        /// false: UI menüsünden gizlenir, global klavye dinleyicisi ve arka plan işlemleri çalışmaz.
        /// true: Tekrar aktif hale gelir.
        /// </summary>
        public static bool EnableGameReplay { get; set; } = false;

        /// <summary>
        /// Sesli Çeviri (Gerçek zamanlı mikrofon ve hoparlör çevirisi)
        /// false: UI menüsünden gizlenir ve ses yakalama süreçleri çalışmaz.
        /// true: Tekrar aktif hale gelir.
        /// </summary>
        public static bool EnableVoiceTranslation { get; set; } = false;
    }
}
