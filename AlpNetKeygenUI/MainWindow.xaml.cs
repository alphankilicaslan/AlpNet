using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AlpNetKeygenUI
{
    public partial class MainWindow : Window
    {
        private const string SecretKey = "AlpNetSecret2026!PulseCore";
        private string _historyFile;
        private string _bannedHistoryFile;
        private List<LicenseHistoryItem> _history = new List<LicenseHistoryItem>();
        private List<LicenseHistoryItem> _bannedHistory = new List<LicenseHistoryItem>();
        private LicenseHistoryItem _itemToBan;
        private LicenseHistoryItem _itemToRename;

        public MainWindow()
        {
            InitializeComponent();
            
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string alpNetDir = Path.Combine(appData, "AlpNet");
            if (!Directory.Exists(alpNetDir)) Directory.CreateDirectory(alpNetDir);
            _historyFile = Path.Combine(alpNetDir, "keygen_history.json");
            _bannedHistoryFile = Path.Combine(alpNetDir, "banned_history.json");

            LoadHistory();
            LoadBannedHistory();
        }

        private void LoadHistory()
        {
            if (File.Exists(_historyFile))
            {
                try
                {
                    string json = File.ReadAllText(_historyFile);
                    var list = JsonSerializer.Deserialize<List<LicenseHistoryItem>>(json);
                    if (list != null)
                    {
                        _history = list;
                        LstHistory.ItemsSource = _history;
                    }
                }
                catch { }
            }
            
            UpdateDashboardStats();
        }

        private void LoadBannedHistory()
        {
            if (File.Exists(_bannedHistoryFile))
            {
                try
                {
                    string json = File.ReadAllText(_bannedHistoryFile);
                    var list = JsonSerializer.Deserialize<List<LicenseHistoryItem>>(json);
                    if (list != null)
                    {
                        _bannedHistory = list;
                        LstBlacklist.ItemsSource = _bannedHistory;
                    }
                }
                catch { }
            }
        }

        private void SaveBannedHistory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_bannedHistory, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_bannedHistoryFile, json);
            }
            catch { }
        }

        private void UpdateDashboardStats()
        {
            decimal totalRevenue = 0;
            int activeSubs = 0;
            int totalLicenses = _history.Count;

            foreach (var item in _history)
            {
                totalRevenue += item.Price;
                if (!item.IsExpired)
                {
                    activeSubs++;
                }
            }

            TxtTotalRevenue.Text = $"₺{totalRevenue:0.00}";
            TxtActiveSubs.Text = activeSubs.ToString();
            TxtTotalLicenses.Text = totalLicenses.ToString();
        }

        private void SaveHistory()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_history, options);
                File.WriteAllText(_historyFile, json);
            }
            catch { }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string hwid = TxtHwid.Text.Trim();
                if (string.IsNullOrEmpty(hwid))
                {
                    MessageBox.Show("Lütfen hedef Donanım Kimliğini (HWID) girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int hours = 720;
                if (CmbDuration.SelectedIndex == 0) hours = 1;
                else if (CmbDuration.SelectedIndex == 1) hours = 6;
                else if (CmbDuration.SelectedIndex == 2) hours = 12;
                else if (CmbDuration.SelectedIndex == 3) hours = 24;
                else if (CmbDuration.SelectedIndex == 4) hours = 24 * 7;
                else if (CmbDuration.SelectedIndex == 5) hours = 24 * 30;
                else if (CmbDuration.SelectedIndex == 6) hours = 24 * 365;
                else if (CmbDuration.SelectedIndex == 7) hours = 24 * 36500; // Unlimited

                // Eski/Süresi dolmuş anahtarları aynı HWID için sil
                _history.RemoveAll(item => 
                {
                    if (item.IsExpired) 
                    {
                        try {
                            string dec = Encoding.UTF8.GetString(Convert.FromBase64String(item.KeyString));
                            return dec.StartsWith(hwid + "|");
                        } catch { return false; }
                    }
                    return false;
                });

                long expTicks = DateTime.Now.AddHours(hours).Ticks;
                string payload = $"{hwid}|{expTicks}";
                string signature = ComputeHmacSha256(payload, SecretKey);

                string finalKey = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{signature}"));

                TxtOutputKey.Text = finalKey;
                TxtStatus.Text = "Lisans başarıyla üretildi!";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 135));

                // Save to history
                string note = TxtNote.Text.Trim();
                if (string.IsNullOrEmpty(note)) note = "-";

                string durationStr = (CmbDuration.SelectedItem as ComboBoxItem)?.Content?.ToString();
                DateTime expireDate = DateTime.Now;
                decimal price = 0;
                if (decimal.TryParse(TxtPrice.Text, out decimal parsedPrice))
                {
                    price = parsedPrice;
                }

                if (durationStr == "1 Saatlik") { expireDate = DateTime.Now.AddHours(1); }
                else if (durationStr == "6 Saatlik") { expireDate = DateTime.Now.AddHours(6); }
                else if (durationStr == "12 Saatlik") { expireDate = DateTime.Now.AddHours(12); }
                else if (durationStr == "Günlük (24 Saat)") { expireDate = DateTime.Now.AddDays(1); }
                else if (durationStr == "Haftalık (7 Gün)") { expireDate = DateTime.Now.AddDays(7); }
                else if (durationStr == "Aylık (30 Gün)") { expireDate = DateTime.Now.AddDays(30); }
                else if (durationStr == "Yıllık (365 Gün)") { expireDate = DateTime.Now.AddDays(365); }
                else if (durationStr == "Sınırsız (Ömür Boyu)") { expireDate = DateTime.Now.AddYears(100); }

                _history.Insert(0, new LicenseHistoryItem
                {
                    CreatedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
                    Note = note,
                    Duration = durationStr,
                    KeyString = finalKey,
                    ExpiresAtTicks = expireDate.Ticks,
                    Price = price
                });
                
                LstHistory.ItemsSource = null;
                LstHistory.ItemsSource = _history;
                SaveHistory();
                UpdateDashboardStats();

                _ = PushLicenseToFirebase(hwid, finalKey);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Hata: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Üretim başarısız.";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 8, 68));
            }
        }

        private async System.Threading.Tasks.Task PushLicenseToFirebase(string hwid, string key)
        {
            try
            {
                string firebaseUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/users/{hwid}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                var data = new { license_key = key };
                using (var client = new System.Net.Http.HttpClient())
                {
                    var content = new System.Net.Http.StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
                    await client.PatchAsync(firebaseUrl, content);
                }
            }
            catch { }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtOutputKey.Text))
            {
                Clipboard.SetText(TxtOutputKey.Text);
                TxtStatus.Text = "Şifre panoya kopyalandı!";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 242, 254));
            }
        }

        private void DeleteExpired_Click(object sender, RoutedEventArgs e)
        {
            long currentTicks = DateTime.Now.Ticks;
            var expiredItems = _history.Where(h => h.ExpiresAtTicks < currentTicks).ToList();

            if (expiredItems.Count > 0)
            {
                var result = MessageBox.Show($"Süresi dolmuş toplam {expiredItems.Count} lisans bulundu.\n\nBunları veritabanından kalıcı olarak silmek istiyor musunuz?", "Onay", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    foreach (var item in expiredItems)
                    {
                        _history.Remove(item);
                    }
                    LstHistory.ItemsSource = null;
                    LstHistory.ItemsSource = _history;
                    SaveHistory();
                    UpdateDashboardStats();
                    TxtStatus.Text = $"{expiredItems.Count} adet süresi dolmuş lisans silindi.";
                    TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 242, 254));
                }
            }
            else
            {
                MessageBox.Show("Süresi dolmuş lisans bulunamadı.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ContextMenu_HistoryCopy_Click(object sender, RoutedEventArgs e)
        {
            if (LstHistory.SelectedItem is LicenseHistoryItem item)
            {
                var dummyBtn = new Button { Tag = item };
                HistoryCopy_Click(dummyBtn, e);
            }
        }

        private void ContextMenu_HistoryRename_Click(object sender, RoutedEventArgs e)
        {
            if (LstHistory.SelectedItem is LicenseHistoryItem item)
            {
                _itemToRename = item;
                TxtRenameInput.Text = item.Note;
                RenameOverlay.Visibility = Visibility.Visible;
                TxtRenameInput.Focus();
                TxtRenameInput.SelectAll();
            }
        }

        private void RenameOverlay_Save_Click(object sender, RoutedEventArgs e)
        {
            if (_itemToRename != null)
            {
                _itemToRename.Note = TxtRenameInput.Text.Trim();
                LstHistory.ItemsSource = null;
                LstHistory.ItemsSource = _history;
                SaveHistory();
                TxtStatus.Text = "Lisans notu güncellendi!";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 242, 254));
            }
            RenameOverlay.Visibility = Visibility.Collapsed;
            _itemToRename = null;
        }

        private void RenameOverlay_Cancel_Click(object sender, RoutedEventArgs e)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            _itemToRename = null;
        }

        private void ContextMenu_HistoryDelete_Click(object sender, RoutedEventArgs e)
        {
            if (LstHistory.SelectedItem is LicenseHistoryItem item)
            {
                var result = System.Windows.MessageBox.Show("Bu lisansı geçmişten tamamen silmek istediğinize emin misiniz?", "Onay", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _history.Remove(item);
                    LstHistory.ItemsSource = null;
                    LstHistory.ItemsSource = _history;
                    SaveHistory();
                    UpdateDashboardStats();
                    TxtStatus.Text = "Lisans tamamen silindi!";
                    TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 8, 68));
                }
            }
        }

        private void ContextMenu_BlacklistRemove_Click(object sender, RoutedEventArgs e)
        {
            if (LstBlacklist.SelectedItem is LicenseHistoryItem item)
            {
                var dummyBtn = new Button { Tag = item };
                BlacklistRemove_Click(dummyBtn, e);
            }
        }

        private void ContextMenu_LiveBan_Click(object sender, RoutedEventArgs e)
        {
            if (LstLiveDevices.SelectedItem is ActiveDevice device)
            {
                var dummyBtn = new Button { Tag = device };
                LiveBan_Click(dummyBtn, e);
            }
        }

        private void ContextMenu_LiveCopyHwid_Click(object sender, RoutedEventArgs e)
        {
            if (LstLiveDevices.SelectedItem is ActiveDevice device)
            {
                var dummyBtn = new Button { Tag = device };
                LiveCopyHwid_Click(dummyBtn, e);
            }
        }

        private void LiveCopyHwid_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ActiveDevice device)
            {
                Clipboard.SetText(device.Hwid);
                TxtStatus.Text = "Cihaz HWID panoya kopyalandı!";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 242, 254));
            }
        }

        private void HistoryCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LicenseHistoryItem item)
            {
                Clipboard.SetText(item.KeyString);
                TxtStatus.Text = "Geçmiş lisans panoya kopyalandı!";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 242, 254));
            }
        }

        private void HistoryDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LicenseHistoryItem item)
            {
                _itemToBan = item;
                BanOverlay.Visibility = Visibility.Visible;
            }
        }

        private async void BanOverlay_Key_Click(object sender, RoutedEventArgs e)
        {
            BanOverlay.Visibility = Visibility.Collapsed;
            if (_itemToBan == null) return;
            
            await ProcessBan(_itemToBan, _itemToBan.KeyString, "SİLİNDİ (ANAHTAR)");
        }

        private async void BanOverlay_HWID_Click(object sender, RoutedEventArgs e)
        {
            BanOverlay.Visibility = Visibility.Collapsed;
            if (_itemToBan == null) return;
            
            string hwid = "";
            try
            {
                string rawKey = _itemToBan.KeyString.Replace("-", "").Replace("ALPNETPULSE", "");
                byte[] decodedBytes = Convert.FromBase64String(rawKey);
                string fullKeyData = Encoding.UTF8.GetString(decodedBytes);
                string[] parts = fullKeyData.Split('|');
                if (parts.Length > 0)
                {
                    hwid = parts[0];
                }
            }
            catch { }

            if (string.IsNullOrEmpty(hwid))
            {
                MessageBox.Show("Bu lisanstan cihaz kimliği (HWID) çıkarılamadı. Mecburen sadece Anahtar yasaklanacak.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                hwid = _itemToBan.KeyString;
            }

            await ProcessBan(_itemToBan, hwid, "YASAKLI (HWID)");
        }

        private void BanOverlay_Cancel_Click(object sender, RoutedEventArgs e)
        {
            BanOverlay.Visibility = Visibility.Collapsed;
            _itemToBan = null;
        }

        private async Task ProcessBan(LicenseHistoryItem item, string targetToBan, string banReason)
        {
            if (!string.IsNullOrEmpty(targetToBan))
            {
                try
                {
                    string firebaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/blacklist.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                    List<string> blacklist = new List<string>();
                    
                    using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        var response = await client.GetAsync(firebaseUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            string bJson = await response.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(bJson) && bJson != "null")
                            {
                                var blist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(bJson);
                                if (blist != null) blacklist = blist;
                            }
                        }
                        
                        if (!blacklist.Contains(targetToBan))
                        {
                            blacklist.Add(targetToBan);
                            var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(blacklist), Encoding.UTF8, "application/json");
                            await client.PutAsync(firebaseUrl, content);
                        }
                    }
                }
                catch { }
            }

            _history.Remove(item);
            LstHistory.ItemsSource = null;
            LstHistory.ItemsSource = _history;
            SaveHistory();

            // Add to banned history
            item.Note = banReason + " - " + item.Note;
            _bannedHistory.Insert(0, item);
            LstBlacklist.ItemsSource = null;
            LstBlacklist.ItemsSource = _bannedHistory;
            SaveBannedHistory();

            TxtStatus.Text = banReason + " eklendi!";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 60, 48));
            UpdateDashboardStats();
            _itemToBan = null;
        }

        private async void BlacklistRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LicenseHistoryItem item)
            {
                string targetToUnban = "";
                bool isHwidBan = item.Note.StartsWith("YASAKLI (HWID)");
                
                if (isHwidBan)
                {
                    try
                    {
                        string rawKey = item.KeyString.Replace("-", "").Replace("ALPNETPULSE", "");
                        byte[] decodedBytes = Convert.FromBase64String(rawKey);
                        string fullKeyData = Encoding.UTF8.GetString(decodedBytes);
                        string[] parts = fullKeyData.Split('|');
                        if (parts.Length > 0)
                        {
                            targetToUnban = parts[0];
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(targetToUnban)) targetToUnban = item.KeyString;
                }
                else
                {
                    targetToUnban = item.KeyString;
                }

                if (!string.IsNullOrEmpty(targetToUnban))
                {
                    try
                    {
                        string firebaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/blacklist.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                        List<string> blacklist = new List<string>();
                        
                        using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                        {
                            var response = await client.GetAsync(firebaseUrl);
                            if (response.IsSuccessStatusCode)
                            {
                                string bJson = await response.Content.ReadAsStringAsync();
                                if (!string.IsNullOrWhiteSpace(bJson) && bJson != "null")
                                {
                                    var blist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(bJson);
                                    if (blist != null) blacklist = blist;
                                }
                            }
                            
                            if (blacklist.Contains(targetToUnban))
                            {
                                blacklist.Remove(targetToUnban);
                                var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(blacklist), Encoding.UTF8, "application/json");
                                await client.PutAsync(firebaseUrl, content);
                            }
                        }
                    }
                    catch { }
                }

                _bannedHistory.Remove(item);
                
                if (item.Note.StartsWith("YASAKLI (HWID) - "))
                    item.Note = item.Note.Substring("YASAKLI (HWID) - ".Length);
                else if (item.Note.StartsWith("SİLİNDİ (ANAHTAR) - "))
                    item.Note = item.Note.Substring("SİLİNDİ (ANAHTAR) - ".Length);

                _history.Insert(0, item);

                LstBlacklist.ItemsSource = null;
                LstBlacklist.ItemsSource = _bannedHistory;
                SaveBannedHistory();

                LstHistory.ItemsSource = null;
                LstHistory.ItemsSource = _history;
                SaveHistory();

                TxtStatus.Text = "Yasaklama (Ban) başarıyla kaldırıldı!";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 135));
                UpdateDashboardStats();
            }
        }

        private async void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (LstHistory == null || LstBlacklist == null || LstLiveDevices == null || PanelSettings == null) return;

            if (TabHistory.IsChecked == true)
            {
                LstHistory.Visibility = Visibility.Visible;
                LstBlacklist.Visibility = Visibility.Collapsed;
                PanelLiveDevices.Visibility = Visibility.Collapsed;
                PanelSettings.Visibility = Visibility.Collapsed;
            }
            else if (TabBlacklist.IsChecked == true)
            {
                LstHistory.Visibility = Visibility.Collapsed;
                LstBlacklist.Visibility = Visibility.Visible;
                PanelLiveDevices.Visibility = Visibility.Collapsed;
                PanelSettings.Visibility = Visibility.Collapsed;
            }
            else if (TabLiveDevices.IsChecked == true)
            {
                LstHistory.Visibility = Visibility.Collapsed;
                LstBlacklist.Visibility = Visibility.Collapsed;
                PanelLiveDevices.Visibility = Visibility.Visible;
                PanelSettings.Visibility = Visibility.Collapsed;
                await FetchLiveDevices();
            }
            else if (TabSettings.IsChecked == true)
            {
                LstHistory.Visibility = Visibility.Collapsed;
                LstBlacklist.Visibility = Visibility.Collapsed;
                PanelLiveDevices.Visibility = Visibility.Collapsed;
                PanelSettings.Visibility = Visibility.Visible;
                await FetchAppState();
            }
        }

        private async System.Threading.Tasks.Task FetchLiveDevices()
        {
            try
            {
                string firebaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/users.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var response = await client.GetAsync(firebaseUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, FirebaseUserDto>>(json);
                            var activeList = new List<ActiveDevice>();
                            
                            if (dict != null)
                            {
                                foreach (var kvp in dict)
                                {
                                    var dto = kvp.Value;
                                    var device = new ActiveDevice
                                    {
                                        Hwid = dto.hwid ?? kvp.Key,
                                        LicenseKey = dto.license_key ?? "-",
                                        Version = dto.version ?? "-",
                                    };

                                    if (DateTime.TryParse(dto.last_seen, out DateTime lastSeen))
                                    {
                                        device.LastSeen = lastSeen;
                                    }

                                    if (device.LicenseKey.StartsWith("TRIAL-"))
                                    {
                                        device.Note = "Ücretsiz Deneme";
                                        device.Duration = "1 Saatlik";
                                    }
                                    else
                                    {
                                        var histItem = _history.Find(h => h.KeyString == device.LicenseKey);
                                        if (histItem != null)
                                        {
                                            device.Note = histItem.Note;
                                            device.Duration = histItem.Duration;
                                        }
                                        else
                                        {
                                            device.Note = "-";
                                            device.Duration = "-";
                                        }
                                    }

                                    activeList.Add(device);
                                }
                            }

                            // Sort by last seen descending (newest online first)
                            activeList.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
                            LstLiveDevices.ItemsSource = activeList;
                        }
                    }
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task FetchAppState()
        {
            try
            {
                string firebaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/appstate.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var response = await client.GetAsync(firebaseUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            var state = JsonSerializer.Deserialize<AppState>(json);
                            if (state != null)
                            {
                                ChkMaintenance.IsChecked = state.maintenance;
                                TxtMaintenanceMsg.Text = state.maintenanceMsg;
                                TxtBroadcastMsg.Text = state.broadcastMsg;
                                TxtVersion.Text = state.version;
                                TxtUpdateUrl.Text = state.updateUrl;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private async Task PatchStateAsync(object state, string successMsg)
        {
            TxtSettingsStatus.Text = "Kaydediliyor...";
            TxtSettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0)); // Orange
            
            try
            {
                string json = JsonSerializer.Serialize(state);
                string firebaseUrl = "https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/appstate.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";

                using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
                    var response = await client.PatchAsync(firebaseUrl, content);
                    if (response.IsSuccessStatusCode)
                    {
                        TxtSettingsStatus.Text = successMsg;
                        TxtSettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 135)); // Green
                    }
                    else
                    {
                        TxtSettingsStatus.Text = "Hata: " + response.StatusCode;
                        TxtSettingsStatus.Foreground = new SolidColorBrush(Colors.Red);
                    }
                }
            }
            catch (Exception ex)
            {
                TxtSettingsStatus.Text = "Bağlantı Hatası: " + ex.Message;
                TxtSettingsStatus.Foreground = new SolidColorBrush(Colors.Red);
            }
        }

        private async void SaveMaintenance_Click(object sender, RoutedEventArgs e)
        {
            await PatchStateAsync(new { maintenance = ChkMaintenance.IsChecked == true, maintenanceMsg = TxtMaintenanceMsg.Text.Trim() }, "Bakım modu güncellendi!");
        }

        private async void SaveBroadcast_Click(object sender, RoutedEventArgs e)
        {
            await PatchStateAsync(new { broadcastMsg = TxtBroadcastMsg.Text.Trim() }, "Duyuru yayınlandı!");
        }

        private async void SaveUpdate_Click(object sender, RoutedEventArgs e)
        {
            await PatchStateAsync(new { version = TxtVersion.Text.Trim(), updateUrl = TxtUpdateUrl.Text.Trim() }, "Yeni sürüm yayınlandı!");
        }

        private async void ClearBroadcast_Click(object sender, RoutedEventArgs e)
        {
            TxtBroadcastMsg.Text = "";
            await PatchStateAsync(new { broadcastMsg = "" }, "Duyuru kaldırıldı!");
        }

        private async void SendDirectMsg_Click(object sender, RoutedEventArgs e)
        {
            if (LstLiveDevices.SelectedItem is ActiveDevice selectedDevice)
            {
                string msg = TxtDirectMsg.Text.Trim();
                if (string.IsNullOrEmpty(msg)) return;

                try
                {
                    string firebaseUrl = $"https://alpnet-d0bc4-default-rtdb.europe-west1.firebasedatabase.app/messages/{selectedDevice.Hwid}.json?auth=ExNWZwF2CkByKFILhfQ2BbTpuxYFlFAUO8quWwfc";
                    var contentObj = new { message = msg };
                    string json = JsonSerializer.Serialize(contentObj);

                    using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
                        var response = await client.PutAsync(firebaseUrl, content);
                        if (response.IsSuccessStatusCode)
                        {
                            TxtDirectMsgStatus.Text = "Mesaj Gönderildi!";
                            TxtDirectMsgStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 135));
                            TxtDirectMsg.Text = "";
                            
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(3000);
                                Dispatcher.Invoke(() => TxtDirectMsgStatus.Text = "");
                            });
                        }
                        else
                        {
                            TxtDirectMsgStatus.Text = "Gönderilemedi.";
                            TxtDirectMsgStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 8, 68));
                        }
                    }
                }
                catch { }
            }
            else
            {
                System.Windows.MessageBox.Show("Lütfen listeden mesaj göndermek istediğiniz cihazı seçin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LiveBan_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ActiveDevice device)
            {
                // Create a mock LicenseHistoryItem to feed into the Ban system
                _itemToBan = new LicenseHistoryItem
                {
                    KeyString = device.LicenseKey,
                    Note = "CANLI CİHAZ (HWID: " + device.Hwid + ")"
                };
                
                // Add to history so it can be moved to blacklist
                _history.Insert(0, _itemToBan);
                
                BanOverlay.Visibility = Visibility.Visible;
            }
        }

        private string ComputeHmacSha256(string data, string key)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                return Convert.ToBase64String(hash);
            }
        }
    }

    public class LicenseHistoryItem
    {
        public string CreatedAt { get; set; } = "";
        public string Note { get; set; } = "";
        public string Duration { get; set; } = "";
        public string KeyString { get; set; } = "";
        public long ExpiresAtTicks { get; set; } = 0;
        public decimal Price { get; set; } = 0;

        [System.Text.Json.Serialization.JsonIgnore]
        public string FormattedPrice => $"₺{Price:0.00}";

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsExpired => ExpiresAtTicks > 0 && DateTime.Now.Ticks > ExpiresAtTicks;

        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusText => IsExpired ? "Süresi Doldu" : (ExpiresAtTicks == 0 ? "Belirsiz" : "Aktif");

        [System.Text.Json.Serialization.JsonIgnore]
        public SolidColorBrush StatusColor => IsExpired ? new SolidColorBrush(Color.FromRgb(255, 8, 68)) : (ExpiresAtTicks == 0 ? new SolidColorBrush(Color.FromRgb(173, 181, 189)) : new SolidColorBrush(Color.FromRgb(0, 255, 135)));
    }

    public class FirebaseUserDto
    {
        public string hwid { get; set; }
        public string license_key { get; set; }
        public string last_seen { get; set; }
        public string version { get; set; }
    }

    public class ActiveDevice
    {
        public string Hwid { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public string Note { get; set; } = "";
        public string Duration { get; set; } = "";
        public string Version { get; set; } = "-";
        public DateTime LastSeen { get; set; } = DateTime.MinValue;

        public bool IsOnline => (DateTime.UtcNow - LastSeen).TotalMinutes <= 3;

        public string Status => IsOnline ? "Çevrimiçi" : "Çevrimdışı";
        
        public string LastSeenText => LastSeen == DateTime.MinValue ? "Bilinmiyor" : LastSeen.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        public SolidColorBrush StatusColor => IsOnline ? new SolidColorBrush(Color.FromRgb(0, 255, 135)) : new SolidColorBrush(Color.FromRgb(173, 181, 189));
    }

    public class AppState
    {
        public bool maintenance { get; set; } = false;
        public string maintenanceMsg { get; set; } = "Sunucularımızda bakım çalışması yapılıyor. Lütfen daha sonra tekrar deneyin.";
        public string broadcastMsg { get; set; } = "";
        public string version { get; set; } = "1.0.0";
        public string updateUrl { get; set; } = "https://alpnet.com/download";
    }
}