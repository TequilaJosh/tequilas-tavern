using System.Diagnostics;
using System.IO;
using System.Windows;
using GameTracker.Services;
using Microsoft.Win32;

namespace GameTracker.Views
{
    /// <summary>
    /// First-run (and re-activation) gate: the app won't open until a valid license key
    /// is entered. Set <see cref="BuyUrl"/> to your Etsy listing so buyers can find it.
    /// </summary>
    public partial class ActivationWindow : Window
    {
        // TODO: point this at your Etsy shop / listing so "Where do I buy?" opens it.
        private const string BuyUrl = "https://www.etsy.com/";

        public LicenseService.LicenseInfo? Activated { get; private set; }

        public ActivationWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => KeyBox.Focus();
        }

        private async void Activate_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            ActivateBtn.IsEnabled = false;
            var original = ActivateBtn.Content;
            ActivateBtn.Content = "Activating…";
            try
            {
                var (reason, info) = await LicenseService.ActivateAsync(KeyBox.Text);
                if (reason == null)
                {
                    Activated = info;
                    DialogResult = true;
                    Close();
                    return;
                }
                ErrorText.Text = reason;
                ErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                ActivateBtn.Content = original;
                ActivateBtn.IsEnabled = true;
            }
        }

        private void LoadFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open license file",
                Filter = "License file (*.lic;*.txt)|*.lic;*.txt|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            try { KeyBox.Text = File.ReadAllText(dlg.FileName).Trim(); }
            catch { ErrorText.Text = "Couldn't read that file."; ErrorText.Visibility = Visibility.Visible; }
        }

        private void Buy_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(BuyUrl) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
