using System;
using System.Windows;

namespace AlpNetWebSetup
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show("AlpNet Kurulum Hatası: " + e.Exception.Message, "AlpNet Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    MessageBox.Show("Kritik Kurulum Hatası: " + ex.Message, "AlpNet Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
        }
    }
}

