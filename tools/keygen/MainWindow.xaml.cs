using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TequilasTavern.Keygen;

public partial class MainWindow : Window
{
    private string? _privateXml;
    private string? _privatePath;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => AutoLoadKey();
    }

    // Find the existing signing key (the one whose public half is embedded in the app).
    private void AutoLoadKey()
    {
        var path = FindPrivateKey();
        if (path != null) LoadKeyFrom(path);
        else SetStatus("No signing key found. Click “Load key…” to point at license_private.xml, "
                       + "or “New keypair…” only if you haven't sold any keys yet.");
    }

    private static string? FindPrivateKey()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, "license_private.xml");
                if (File.Exists(p)) return p;
                var p2 = Path.Combine(dir.FullName, "tools", "keygen", "license_private.xml");
                if (File.Exists(p2)) return p2;
            }
        }
        return null;
    }

    private void LoadKeyFrom(string path)
    {
        try
        {
            var xml = File.ReadAllText(path);
            if (!xml.Contains("<D>"))   // a private RSA key XML has private params (<D>, <P>, ...)
            {
                SetStatus("That file is a PUBLIC key. You need the PRIVATE key (license_private.xml).", true);
                return;
            }
            _privateXml = xml;
            _privatePath = path;
            KeyStatus.Text = path;
            KeyStatus.ToolTip = path;
            SetStatus("Signing key loaded. Fill in the buyer details and generate a key.");
        }
        catch (Exception ex) { SetStatus("Couldn't read that key: " + ex.Message, true); }
    }

    private void LoadKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open license_private.xml",
            Filter = "Private key (*.xml)|*.xml|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) LoadKeyFrom(dlg.FileName);
    }

    private void NewKeypair_Click(object sender, RoutedEventArgs e)
    {
        var warn = MessageBox.Show(this,
            "Only do this if you have NOT sold any keys yet.\n\n"
            + "A new keypair invalidates every key made with the old one, and you'd have to rebuild "
            + "the app with the new public key (Assets/license_public.xml).\n\nCreate a new keypair?",
            "New keypair", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (warn != MessageBoxResult.Yes) return;

        var dlg = new SaveFileDialog
        {
            Title = "Save the new PRIVATE key",
            FileName = "license_private.xml",
            Filter = "Private key (*.xml)|*.xml",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var (priv, pub) = KeyMaker.GenerateKeypair();
            File.WriteAllText(dlg.FileName, priv);
            var pubPath = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, "license_public.xml");
            File.WriteAllText(pubPath, pub);
            LoadKeyFrom(dlg.FileName);
            SetStatus($"New keypair created. Rebuild the app after replacing Assets/license_public.xml with:\n{pubPath}");
        }
        catch (Exception ex) { SetStatus("Couldn't create the keypair: " + ex.Message, true); }
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        string email = EmailBox.Text.Trim();
        if (name.Length == 0 || email.Length == 0)
        {
            SetStatus("Enter at least a buyer name and email (or use the Friend / gift key button).", true);
            return;
        }
        int max = int.TryParse(MaxBox.Text.Trim(), out var m) && m > 0 ? m : 2;
        int days = int.TryParse(DaysBox.Text.Trim(), out var d) && d > 0 ? d : 0;
        Emit(name, email, OrderBox.Text.Trim(), days, max, "buyer");
    }

    // One-click key for a friend/gift — no details required.
    private void FriendKey_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) name = "Friend";
        int max = int.TryParse(MaxBox.Text.Trim(), out var m) && m > 0 ? m : 2;
        Emit(name, EmailBox.Text.Trim(), "gift", 0, max, "gift");
    }

    private void Emit(string name, string email, string order, int days, int max, string kind)
    {
        if (_privateXml == null) { SetStatus("Load your signing key first (Load key…).", true); return; }
        try
        {
            var token = KeyMaker.Issue(_privateXml, name, email, order, days, max);
            OutBox.Text = token;
            CopyBtn.IsEnabled = SaveBtn.IsEnabled = true;
            var who = kind == "gift" ? $"Gift key ({name})" : $"Key for {name}";
            SetStatus($"{who} — {(days > 0 ? days + "-day" : "perpetual")}, up to {max} device(s). "
                      + "Click Copy, then send it.");
        }
        catch (Exception ex) { SetStatus("Couldn't generate the key: " + ex.Message, true); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (OutBox.Text.Length == 0) return;
        try { Clipboard.SetText(OutBox.Text); SetStatus("Copied to clipboard."); }
        catch { SetStatus("Couldn't access the clipboard — select the text and copy manually.", true); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (OutBox.Text.Length == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save license file",
            FileName = SafeName(EmailBox.Text) + ".lic",
            Filter = "License file (*.lic)|*.lic|Text file (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, OutBox.Text); SetStatus("Saved " + dlg.FileName); }
        catch (Exception ex) { SetStatus("Couldn't save: " + ex.Message, true); }
    }

    private static string SafeName(string s)
    {
        s = (s ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "license" : s;
    }

    private void SetStatus(string msg, bool error = false)
    {
        Status.Text = msg;
        Status.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Gold" : "Dim");
    }
}
