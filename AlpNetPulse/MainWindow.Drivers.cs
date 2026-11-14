using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AlpNetPulse
{
    public partial class MainWindow : Window
    {
        private List<DriverInfo>? _scannedDrivers;
        private string _activeDriverFilter = "ALL"; // ALL, GPU, CPU_MB, NET, HISTORY

        // ═══════════════════════════════════════════════════════════
        //  OTOMATİK SÜRÜCÜ TARAMA (SAYFA İLK AÇILDIĞINDA)
        // ═══════════════════════════════════════════════════════════
        private async Task AutoScanDriversOnOpenAsync()
        {
            await RunDriverScanWorkflowAsync();
        }

        private async void BtnScanDrivers_Click(object sender, RoutedEventArgs e)
        {
            await RunDriverScanWorkflowAsync();
        }

        private async Task RunDriverScanWorkflowAsync()
        {
            try
            {
                BtnScanDrivers.IsEnabled = false;
                BtnScanDrivers.Content = "⏳ Taranıyor...";
                BtnUpdateAllDrivers.IsEnabled = false;
                PnlDriverProgress.Visibility = Visibility.Visible;
                DriverProgressBar.Value = 10;
                TxtDriverProgressPct.Text = "%10";
                TxtDriverProgressStatus.Text = "Donanım bilgileri ve sürücüler sorgulanıyor...";

                var drivers = await DriverManager.Instance.ScanDriversAsync(status =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtDriverProgressStatus.Text = status;
                        if (DriverProgressBar.Value < 90)
                        {
                            DriverProgressBar.Value += 18;
                            TxtDriverProgressPct.Text = $"%{(int)DriverProgressBar.Value}";
                        }
                    });
                });

                _scannedDrivers = drivers;

                DriverProgressBar.Value = 100;
                TxtDriverProgressPct.Text = "%100";
                TxtDriverProgressStatus.Text = "Tarama tamamlandı!";

                await Task.Delay(400);
                PnlDriverProgress.Visibility = Visibility.Collapsed;

                UpdateDriverStatsCounters(drivers);
                RenderDriversListUi();

                BtnScanDrivers.Content = "🔍 SÜRÜCÜLERİ TARA";
                BtnScanDrivers.IsEnabled = true;
                BtnUpdateAllDrivers.IsEnabled = drivers.Any(d => d.IsUpdateAvailable);
            }
            catch (Exception ex)
            {
                TxtDriverProgressStatus.Text = $"Tarama hatası: {ex.Message}";
                BtnScanDrivers.Content = "🔍 TEKRAR TARA";
                BtnScanDrivers.IsEnabled = true;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  İSTATİSTİK VE ROZET GÜNCELLEME
        // ═══════════════════════════════════════════════════════════
        private void UpdateDriverStatsCounters(List<DriverInfo> drivers)
        {
            foreach (var d in drivers)
            {
                if (!DriverManager.IsValidDriverUpgrade(d, d.LatestVersion, d.LatestDate))
                {
                    d.IsUpdateAvailable = false;
                }
            }

            int total = drivers.Count;
            int upToDate = drivers.Count(d => !d.IsUpdateAvailable);
            int pending = drivers.Count(d => d.IsUpdateAvailable);

            TxtStatTotalDrivers.Text = total.ToString();
            TxtStatUpToDate.Text = upToDate.ToString();
            TxtStatUpdatesAvail.Text = pending.ToString();

            if (pending > 0)
            {
                BadgeDriverHealth.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 242, 254));
                TxtDriverHealthBadge.Text = $"{pending} GÜNCELLEME";
                TxtDriverHealthBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
            }
            else
            {
                BadgeDriverHealth.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 255, 135));
                TxtDriverHealthBadge.Text = "TÜMÜ GÜNCEL";
                TxtDriverHealthBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  FİLTRE SEKMELERİ
        // ═══════════════════════════════════════════════════════════
        private void TabDriverFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            TabDriverAll.Background = System.Windows.Media.Brushes.Transparent;
            TabDriverAll.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
            TabDriverGpu.Background = System.Windows.Media.Brushes.Transparent;
            TabDriverGpu.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
            TabDriverCpuMb.Background = System.Windows.Media.Brushes.Transparent;
            TabDriverCpuMb.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
            TabDriverNet.Background = System.Windows.Media.Brushes.Transparent;
            TabDriverNet.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));
            TabDriverHistory.Background = System.Windows.Media.Brushes.Transparent;
            TabDriverHistory.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189));

            btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
            btn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));

            if (btn == TabDriverAll) _activeDriverFilter = "ALL";
            else if (btn == TabDriverGpu) _activeDriverFilter = "GPU";
            else if (btn == TabDriverCpuMb) _activeDriverFilter = "CPU_MB";
            else if (btn == TabDriverNet) _activeDriverFilter = "NET";
            else if (btn == TabDriverHistory) _activeDriverFilter = "HISTORY";

            if (_activeDriverFilter == "HISTORY")
            {
                PnlDriverCardsContainer.Visibility = Visibility.Collapsed;
                PnlDriverHistoryContainer.Visibility = Visibility.Visible;
                RenderDriverHistoryUi();
            }
            else
            {
                PnlDriverHistoryContainer.Visibility = Visibility.Collapsed;
                PnlDriverCardsContainer.Visibility = Visibility.Visible;
                RenderDriversListUi();
            }
        }

        private void BtnToggleDriverHistory_Click(object sender, RoutedEventArgs e)
        {
            TabDriverFilter_Click(TabDriverHistory, e);
        }

        // ═══════════════════════════════════════════════════════════
        //  SÜRÜCÜ KARTLARINI OLUŞTUR (UI RENDER)
        // ═══════════════════════════════════════════════════════════
        private void RenderDriversListUi()
        {
            PnlDriverCardsContainer.Children.Clear();

            if (_scannedDrivers == null || _scannedDrivers.Count == 0)
            {
                var emptyBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 32, 40)),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(30),
                    Margin = new Thickness(0, 0, 0, 15)
                };
                var emptyStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "🔍 Henüz sürücü taraması yapılmadı.",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "Yukarıdaki 'SÜRÜCÜLERİ TARA' butonuna basarak bilgisayarınızdaki tüm donanımları inceleyebilirsiniz.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189)),
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyBorder.Child = emptyStack;
                PnlDriverCardsContainer.Children.Add(emptyBorder);
                return;
            }

            IEnumerable<DriverInfo> filtered = _scannedDrivers;
            if (_activeDriverFilter == "GPU")
            {
                filtered = filtered.Where(d => d.Category == DriverCategory.GPU);
            }
            else if (_activeDriverFilter == "CPU_MB")
            {
                filtered = filtered.Where(d => d.Category == DriverCategory.CPU || d.Category == DriverCategory.Motherboard);
            }
            else if (_activeDriverFilter == "NET")
            {
                filtered = filtered.Where(d => d.Category == DriverCategory.Network);
            }

            foreach (var drv in filtered)
            {
                if (!DriverManager.IsValidDriverUpgrade(drv, drv.LatestVersion, drv.LatestDate))
                {
                    drv.IsUpdateAvailable = false;
                    drv.Status = DriverStatus.UpToDate;
                    drv.StatusBadgeText = drv.IsBiosFirmware ? "✔ KORUMALI BIOS (UEFI)" : "✔ GÜNCEL (WHQL Doğrulandı)";
                    drv.StatusColorHex = drv.IsBiosFirmware ? "#667EEA" : "#00FF87";
                }
                var card = CreateDriverCard(drv);
                PnlDriverCardsContainer.Children.Add(card);
            }
        }

        private Border CreateDriverCard(DriverInfo drv)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 23, 30)), // #15171E
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 40, 51)), // #252833
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var mainStack = new StackPanel();

            // 1. ÜST BAŞLIK SATIRI (Kategori Rozeti, Donanım Adı, Durum Rozeti)
            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

            var catBadge = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40,
                    Convert.ToByte(drv.CategoryColorHex.Substring(1, 2), 16),
                    Convert.ToByte(drv.CategoryColorHex.Substring(3, 2), 16),
                    Convert.ToByte(drv.CategoryColorHex.Substring(5, 2), 16))),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 8, 0)
            };
            catBadge.Child = new TextBlock
            {
                Text = drv.CategoryBadgeText,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(drv.CategoryColorHex)!
            };
            badgeStack.Children.Add(catBadge);

            if (!string.IsNullOrEmpty(drv.Manufacturer))
            {
                var mfgText = new TextBlock
                {
                    Text = $"ÜRETİCİ: {drv.Manufacturer.ToUpperInvariant()}",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badgeStack.Children.Add(mfgText);
            }
            titleStack.Children.Add(badgeStack);

            titleStack.Children.Add(new TextBlock
            {
                Text = drv.Name,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250))
            });

            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            // Durum Rozeti
            var statusBadge = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(35,
                    Convert.ToByte(drv.StatusColorHex.Substring(1, 2), 16),
                    Convert.ToByte(drv.StatusColorHex.Substring(3, 2), 16),
                    Convert.ToByte(drv.StatusColorHex.Substring(5, 2), 16))),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(drv.StatusColorHex)!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Top
            };
            statusBadge.Child = new TextBlock
            {
                Text = drv.StatusBadgeText,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(drv.StatusColorHex)!
            };
            Grid.SetColumn(statusBadge, 1);
            topGrid.Children.Add(statusBadge);

            mainStack.Children.Add(topGrid);

            // 2. ORTA BİLGİ KUTULARI (Mevcut Sürüm, En Son WHQL Sürüm, Boyut/Tarih)
            var infoGrid = new Grid
            {
                Margin = new Thickness(0, 0, 0, 12)
            };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Kutu 1: Mevcut Sürüm
            var box1 = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 32, 40)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 5, 0)
            };
            var s1 = new StackPanel();
            s1.Children.Add(new TextBlock { Text = "YÜKLÜ SÜRÜM", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189)) });
            s1.Children.Add(new TextBlock { Text = $"v{drv.CurrentVersion}", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250)), Margin = new Thickness(0, 3, 0, 0) });
            s1.Children.Add(new TextBlock { Text = drv.CurrentDateStr, FontSize = 10, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 117, 125)), Margin = new Thickness(0, 2, 0, 0) });
            box1.Child = s1;
            Grid.SetColumn(box1, 0);
            infoGrid.Children.Add(box1);

            // Kutu 2: En Son Sürüm
            var box2 = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 32, 40)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(3, 0, 3, 0)
            };
            var s2 = new StackPanel();
            s2.Children.Add(new TextBlock { Text = "EN SON WHQL SÜRÜM", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(drv.StatusColorHex)! });
            s2.Children.Add(new TextBlock { Text = $"v{drv.LatestVersion}", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(drv.StatusColorHex)!, Margin = new Thickness(0, 3, 0, 0) });
            s2.Children.Add(new TextBlock { Text = drv.LatestDateStr, FontSize = 10, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 117, 125)), Margin = new Thickness(0, 2, 0, 0) });
            box2.Child = s2;
            Grid.SetColumn(box2, 1);
            infoGrid.Children.Add(box2);

            // Kutu 3: Paket Bilgisi / Boyut
            var box3 = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 32, 40)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(5, 0, 0, 0)
            };
            var s3 = new StackPanel();
            s3.Children.Add(new TextBlock { Text = "DAĞITIM / DOSYA", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189)) });
            s3.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(drv.DownloadSize) ? "Windows WHQL" : drv.DownloadSize, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254)), Margin = new Thickness(0, 3, 0, 0) });
            s3.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(drv.InfName) ? "PnP Sürücü Deposu" : drv.InfName, FontSize = 10, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 117, 125)), Margin = new Thickness(0, 2, 0, 0) });
            box3.Child = s3;
            Grid.SetColumn(box3, 2);
            infoGrid.Children.Add(box3);

            mainStack.Children.Add(infoGrid);

            // 3. SÜRÜCÜ NOTU / AÇIKLAMA (Varsa)
            if (!string.IsNullOrEmpty(drv.ReleaseNotes))
            {
                var noteBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 0, 242, 254)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 0, 0, 12)
                };
                noteBorder.Child = new TextBlock
                {
                    Text = $"💡 {drv.ReleaseNotes}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254)),
                    TextWrapping = TextWrapping.Wrap
                };
                mainStack.Children.Add(noteBorder);
            }

            // 4. AKSİYON BUTONLARI SATIRI
            var actionGrid = new Grid();
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (drv.IsBiosFirmware)
            {
                var btnBios = new Button
                {
                    Content = "🛡️ BIOS Koruması & Resmi Kılavuz",
                    Height = 36,
                    Padding = new Thickness(16, 0, 16, 0),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 102, 126, 234)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 126, 234)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 126, 234)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 0, 8, 0),
                    Style = (Style)FindResource("FlatButtonStyle")
                };
                btnBios.Click += (s, e) =>
                {
                    ShowBiosProtectionDialog(drv);
                };
                Grid.SetColumn(btnBios, 0);
                actionGrid.Children.Add(btnBios);
            }
            else
            {
                // Güncelleme Butonu
                var btnUpdate = new Button
                {
                    Height = 36,
                    Padding = new Thickness(16, 0, 16, 0),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 0, 8, 0),
                    Style = (Style)FindResource("FlatButtonStyle")
                };

                bool canUpdate = drv.IsUpdateAvailable && !drv.IsBiosFirmware && DriverManager.IsNewerVersion(drv.LatestVersion, drv.CurrentVersion) && !string.Equals(drv.CurrentVersion, drv.LatestVersion, StringComparison.OrdinalIgnoreCase);

                if (canUpdate)
                {
                    btnUpdate.Content = $"🚀 GÜNCELLE (v{drv.LatestVersion})";
                    btnUpdate.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254));
                    btnUpdate.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));
                    btnUpdate.IsEnabled = true;
                    btnUpdate.Click += async (s, e) =>
                    {
                        await ExecuteSingleDriverUpdateAsync(drv, btnUpdate);
                    };
                }
                else
                {
                    btnUpdate.Content = "✔ GÜNCEL (WHQL Doğrulandı)";
                    btnUpdate.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 42, 53));
                    btnUpdate.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                    btnUpdate.IsEnabled = false;
                }

                Grid.SetColumn(btnUpdate, 0);
                actionGrid.Children.Add(btnUpdate);

                // Eğer son 48 saat içinde alınmış yedeği varsa "Eski Sürücüye Geri Yükle" butonu sun
                if (drv.HasExistingBackup && drv.ExistingBackupRecord != null)
                {
                    var btnRollback = new Button
                    {
                        Content = $"⏪ Eski Sürücüye Geri Yükle (v{drv.ExistingBackupOldVersion})",
                        Height = 36,
                        Padding = new Thickness(14, 0, 14, 0),
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 255, 8, 68)),
                        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68)),
                        BorderThickness = new Thickness(1),
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 92, 170)),
                        FontWeight = FontWeights.Bold,
                        FontSize = 11,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Margin = new Thickness(0, 0, 8, 0),
                        Style = (Style)FindResource("FlatButtonStyle")
                    };
                    btnRollback.Click += async (s, e) =>
                    {
                        var confirm = System.Windows.MessageBox.Show(
                            $"{drv.Name} donanımını AlpNet tarafından 48 saat içinde yedeklenen önceki v{drv.ExistingBackupOldVersion} sürümüne geri yüklemek istiyor musunuz?\n\nYedekleme Tarihi: {drv.ExistingBackupRecord.Timestamp}",
                            "Sürücü Geri Yükleme Onayı", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (confirm != MessageBoxResult.Yes) return;

                        btnRollback.IsEnabled = false;
                        btnRollback.Content = "⏳ Geri Yükleniyor...";
                        var res = await DriverManager.Instance.RollbackDriverAsync(drv.ExistingBackupRecord);
                        System.Windows.MessageBox.Show(res.Message, "Sürücü Geri Yükleme", MessageBoxButton.OK,
                            res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        await RunDriverScanWorkflowAsync();
                    };
                    Grid.SetColumn(btnRollback, 1);
                    actionGrid.Children.Add(btnRollback);
                }
            }

            mainStack.Children.Add(actionGrid);

            border.Child = mainStack;
            return border;
        }

        // ═══════════════════════════════════════════════════════════
        //  TEK BİR SÜRÜCÜYÜ GÜNCELLEME İŞ AKIŞI
        // ═══════════════════════════════════════════════════════════
        private async Task ExecuteSingleDriverUpdateAsync(DriverInfo drv, Button btn)
        {
            try
            {
                btn.IsEnabled = false;
                btn.Content = "⏳ Hazırlanıyor...";
                PnlDriverProgress.Visibility = Visibility.Visible;
                DriverProgressBar.Value = 15;
                TxtDriverProgressPct.Text = "%15";

                var result = await DriverManager.Instance.UpdateDriverAsync(drv, (pct, status) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        DriverProgressBar.Value = pct;
                        TxtDriverProgressPct.Text = $"%{pct}";
                        TxtDriverProgressStatus.Text = status;
                        btn.Content = $"⏳ %{pct}";
                    });
                });

                PnlDriverProgress.Visibility = Visibility.Collapsed;

                if (result.Success)
                {
                    btn.Content = "✔ GÜNCELLENDİ";
                    btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 135));
                    btn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 12, 16));
                    drv.CurrentVersion = drv.LatestVersion;
                    drv.IsUpdateAvailable = false;
                    drv.Status = DriverStatus.UpToDate;
                    drv.StatusBadgeText = "✔ GÜNCEL";
                    drv.StatusColorHex = "#00FF87";

                    System.Windows.MessageBox.Show(result.Message, "AlpNet Sürücü Güncellemesi", MessageBoxButton.OK, MessageBoxImage.Information);
                    RenderDriversListUi();
                }
                else
                {
                    btn.Content = "❌ HATA";
                    btn.IsEnabled = true;
                    System.Windows.MessageBox.Show(result.Message, "Sürücü Güncelleme Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                PnlDriverProgress.Visibility = Visibility.Collapsed;
                btn.IsEnabled = true;
                btn.Content = "🚀 GÜNCELLE";
                System.Windows.MessageBox.Show($"Güncelleme başarısız: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  TÜMÜNÜ GÜNCELLE BUTONU
        // ═══════════════════════════════════════════════════════════
        private async void BtnUpdateAllDrivers_Click(object sender, RoutedEventArgs e)
        {
            if (_scannedDrivers == null) return;
            var outdated = _scannedDrivers.Where(d => d.IsUpdateAvailable && !d.IsBiosFirmware && DriverManager.IsNewerVersion(d.LatestVersion, d.CurrentVersion) && !string.Equals(d.CurrentVersion, d.LatestVersion, StringComparison.OrdinalIgnoreCase)).ToList();
            if (outdated.Count == 0)
            {
                System.Windows.MessageBox.Show("Tüm donanım sürücüleriniz zaten en güncel durumda!", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = System.Windows.MessageBox.Show($"{outdated.Count} adet donanım için güncelleme bulundu:\n\n" +
                string.Join("\n", outdated.Select(d => $"• {d.Name} (v{d.CurrentVersion} -> v{d.LatestVersion})")) +
                "\n\nSürücüler sırayla yedeklenip güncellenecektir. Devam etmek istiyor musunuz?",
                "Tüm Sürücüleri Güncelle", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (dialog != MessageBoxResult.Yes) return;

            BtnUpdateAllDrivers.IsEnabled = false;
            foreach (var drv in outdated)
            {
                await DriverManager.Instance.UpdateDriverAsync(drv, (pct, status) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        PnlDriverProgress.Visibility = Visibility.Visible;
                        DriverProgressBar.Value = pct;
                        TxtDriverProgressPct.Text = $"%{pct}";
                        TxtDriverProgressStatus.Text = $"{drv.Name}: {status}";
                    });
                });
            }

            PnlDriverProgress.Visibility = Visibility.Collapsed;
            BtnUpdateAllDrivers.IsEnabled = true;
            await RunDriverScanWorkflowAsync();
        }

        // ═══════════════════════════════════════════════════════════
        //  TÜM SÜRÜCÜLERİ YEDEKLE BUTONU
        // ═══════════════════════════════════════════════════════════
        private async void BtnBackupAllDrivers_Click(object sender, RoutedEventArgs e)
        {
            BtnBackupAllDrivers.IsEnabled = false;
            BtnBackupAllDrivers.Content = "⏳ Yedekleniyor...";
            PnlDriverProgress.Visibility = Visibility.Visible;
            DriverProgressBar.Value = 30;
            TxtDriverProgressStatus.Text = "Tüm üçüncü parti donanım sürücüleri dışa aktarılıyor (pnputil)...";

            bool ok = await DriverManager.Instance.BackupAllDriversAsync(status =>
            {
                Dispatcher.Invoke(() => TxtDriverProgressStatus.Text = status);
            });

            PnlDriverProgress.Visibility = Visibility.Collapsed;
            BtnBackupAllDrivers.IsEnabled = true;
            BtnBackupAllDrivers.Content = "💾 TÜMÜNÜ YEDEKLE";

            if (ok)
            {
                System.Windows.MessageBox.Show($"Tüm sürücüleriniz AlpNet güvenli deposuna yedeklendi!\nKonum: {DriverManager.Instance.GetBackupsDirectory()}", "Yedekleme Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("Sürücü yedekleme tamamlanamadı. Yönetici izni gerekebilir.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  GÜNCELLEME GEÇMİŞİ VE GERİ YÜKLEME (ROLLBACK) LİSTESİ
        // ═══════════════════════════════════════════════════════════
        private void RenderDriverHistoryUi()
        {
            PnlDriverHistoryItems.Children.Clear();
            var history = DriverManager.Instance.GetUpdateHistory();

            if (history.Count == 0)
            {
                PnlDriverHistoryItems.Children.Add(new TextBlock
                {
                    Text = "Henüz AlpNet üzerinden bir sürücü güncellemesi veya yedeği oluşturulmadı.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(173, 181, 189)),
                    Margin = new Thickness(0, 10, 0, 0)
                });
                return;
            }

            foreach (var record in history)
            {
                if (string.Equals(record.OldVersion, record.NewVersion, StringComparison.OrdinalIgnoreCase) || !DriverManager.IsNewerVersion(record.NewVersion, record.OldVersion))
                {
                    continue;
                }

                var border = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 23, 30)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 40, 51)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(15),
                    Margin = new Thickness(0, 0, 0, 10)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textStack = new StackPanel();
                textStack.Children.Add(new TextBlock
                {
                    Text = $"{record.DeviceName} ({record.Category})",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250))
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = $"Tarih: {record.Timestamp} | Değişim: v{record.OldVersion} -> v{record.NewVersion} | Durum: {record.Status}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 242, 254)),
                    Margin = new Thickness(0, 4, 0, 0)
                });

                Grid.SetColumn(textStack, 0);
                grid.Children.Add(textStack);

                var btnRollback = new Button
                {
                    Content = "⏪ Eski Sürücüye Geri Yükle",
                    Height = 32,
                    Padding = new Thickness(12, 0, 12, 0),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 8, 68)),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Style = (Style)FindResource("FlatButtonStyle")
                };

                btnRollback.Click += async (s, e) =>
                {
                    var confirm = System.Windows.MessageBox.Show($"{record.DeviceName} sürücüsünü eski sürümüne (v{record.OldVersion}) geri yüklemek istiyor musunuz?",
                        "Sürücü Geri Yükleme Onayı", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;

                    btnRollback.IsEnabled = false;
                    btnRollback.Content = "⏳ Geri Yükleniyor...";
                    var rollResult = await DriverManager.Instance.RollbackDriverAsync(record);
                    btnRollback.Content = rollResult.Success ? "✔ GERİ YÜKLENDİ" : "❌ HATA";
                    System.Windows.MessageBox.Show(rollResult.Message, "Sürücü Geri Yükleme", MessageBoxButton.OK,
                        rollResult.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
                    RenderDriverHistoryUi();
                };

                Grid.SetColumn(btnRollback, 1);
                grid.Children.Add(btnRollback);

                border.Child = grid;
                PnlDriverHistoryItems.Children.Add(border);
            }
        }

        private void OpenBackupsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = DriverManager.Instance.GetBackupsDirectory();
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                }
            }
            catch { }
        }

        private void OpenSystemRestore_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Windows Sistem Geri Yükleme aracı başlatılamadı: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowBiosProtectionDialog(DriverInfo drv)
        {
            string mfg = drv.Manufacturer;
            string url = "https://www.google.com/search?q=" + Uri.EscapeDataString($"{mfg} {drv.Name} bios update download");
            if (mfg.Contains("ASUS", StringComparison.OrdinalIgnoreCase)) url = "https://www.asus.com/support/";
            else if (mfg.Contains("MSI", StringComparison.OrdinalIgnoreCase)) url = "https://www.msi.com/support";
            else if (mfg.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase)) url = "https://www.gigabyte.com/Support";
            else if (mfg.Contains("Lenovo", StringComparison.OrdinalIgnoreCase)) url = "https://pcsupport.lenovo.com/";
            else if (mfg.Contains("Dell", StringComparison.OrdinalIgnoreCase)) url = "https://www.dell.com/support/home";
            else if (mfg.Contains("HP", StringComparison.OrdinalIgnoreCase)) url = "https://support.hp.com/";

            string message = "🔒 DONANIM GÜVENLİK KORUMASI: BIOS & FIRMWARE\n\n" +
                             $"Anakart Modeli: {drv.Name}\n" +
                             $"Yüklü BIOS Sürümü: v{drv.CurrentVersion} ({drv.CurrentDateStr})\n\n" +
                             "⚠️ Neden Otomatik Yüklenmez?\n" +
                             "BIOS anakartın donanımsal temel bellenimidir. Windows içinden otomatik flashlama sırasında yaşanabilecek olası bir elektrik kesintisi, mavi ekran veya yazılımsal donma anakartın kalıcı olarak kullanılamaz hale gelmesine (BRICK olmasına) neden olabilir. Windows yazılımları çöken bir anakart BIOS'unu geri kurtaramaz.\n\n" +
                             "Bu nedenle AlpNet, BIOS güncellemelerini donanım güvenliğiniz için otomatik olarak flashlamaz. Bunun yerine anakartınızın yerleşik BIOS EZ-Flash / Q-Flash / M-Flash aracını veya üreticinizin resmi yazılımını kullanmanız gerekmektedir.\n\n" +
                             $"Resmi üretici ({drv.Manufacturer}) destek sayfasına gitmek istiyor musunuz?";

            var res = System.Windows.MessageBox.Show(message, "AlpNet BIOS Güvenlik Koruması", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { }
            }
        }
    }
}
