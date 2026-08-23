using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GameTracker.Models;

namespace GameTracker
{
    /// <summary>
    /// Tequilas' Tavern — the chat + Tavern Tales companion (no game tracking).
    /// A Final Fantasy 1 styled hub that opens the chat room and stream tools.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Services.OverlayService.Initialize();   // starts the overlay server
            Services.OverlayService.Update(null);   // no game tracking: overlay runs in idle mode

            // Quietly check GitHub releases for a newer version on launch.
            Loaded += async (_, _) => await Services.UpdateService.CheckForUpdatesAsync(silent: true);
        }

        // ── Compatibility stubs ─────────────────────────────────────────────
        // Counters and settings were built to scope values per game. The tavern has
        // no game library, so counters act as single global values (game = "").
        public string CurrentGameTitle() => string.Empty;
        public List<string> AllGameTitles() => new();
        public List<(string category, List<string> titles)> GamesByStatus() => new();

        #region Global hotkeys

        private const int WM_HOTKEY = 0x0312;
        private const int HK_FX_STOP = 40;
        private const int HK_REDEEM_BASE = 10;
        private const int MAX_REDEEM_HK = 24;
        private const int HK_COUNTER_INC_BASE = 100;
        private const int HK_COUNTER_DEC_BASE = 140;
        private const int MAX_COUNTER_HK = 24;
        private IntPtr _hwnd;
        private HotkeyConfig _hotkeys = new();

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndHook);
            _hotkeys = Services.SettingsService.LoadHotkeys();
            RegisterHotkeys();
        }

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            RegisterHotKey(_hwnd, HK_FX_STOP, _hotkeys.FxStop.Win32Modifiers, _hotkeys.FxStop.VirtualKey);
            var redeems = Services.SettingsService.LoadChatFeatures().Redeems;
            for (int i = 0; i < redeems.Count && i < MAX_REDEEM_HK; i++)
            {
                var hk = redeems[i].Hotkey;
                if (hk != null && hk.VirtualKey != 0)
                    RegisterHotKey(_hwnd, HK_REDEEM_BASE + i, hk.Win32Modifiers, hk.VirtualKey);
            }
            var counters = Services.SettingsService.LoadCounters();
            for (int i = 0; i < counters.Count && i < MAX_COUNTER_HK; i++)
            {
                var inc = counters[i].IncHotkey;
                var dec = counters[i].DecHotkey;
                if (inc != null && inc.VirtualKey != 0)
                    RegisterHotKey(_hwnd, HK_COUNTER_INC_BASE + i, inc.Win32Modifiers, inc.VirtualKey);
                if (dec != null && dec.VirtualKey != 0)
                    RegisterHotKey(_hwnd, HK_COUNTER_DEC_BASE + i, dec.Win32Modifiers, dec.VirtualKey);
            }
        }

        public void RefreshHotkeys()
        {
            _hotkeys = Services.SettingsService.LoadHotkeys();
            RegisterHotkeys();
        }

        /// <summary>Temporarily release all global hotkeys (so a capture box can read combos).</summary>
        public void PauseHotkeys() => UnregisterHotkeys();

        private void UnregisterHotkeys()
        {
            if (_hwnd == IntPtr.Zero) return;
            UnregisterHotKey(_hwnd, HK_FX_STOP);
            for (int i = 0; i < MAX_REDEEM_HK; i++) UnregisterHotKey(_hwnd, HK_REDEEM_BASE + i);
            for (int i = 0; i < MAX_COUNTER_HK; i++)
            {
                UnregisterHotKey(_hwnd, HK_COUNTER_INC_BASE + i);
                UnregisterHotKey(_hwnd, HK_COUNTER_DEC_BASE + i);
            }
        }

        private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HK_FX_STOP) { HotkeyStopEffects(); handled = true; }
                else if (id >= HK_REDEEM_BASE && id < HK_REDEEM_BASE + MAX_REDEEM_HK)
                { HotkeyRedeem(id - HK_REDEEM_BASE); handled = true; }
                else if (id >= HK_COUNTER_INC_BASE && id < HK_COUNTER_INC_BASE + MAX_COUNTER_HK)
                { CounterBump(id - HK_COUNTER_INC_BASE, +1); handled = true; }
                else if (id >= HK_COUNTER_DEC_BASE && id < HK_COUNTER_DEC_BASE + MAX_COUNTER_HK)
                { CounterBump(id - HK_COUNTER_DEC_BASE, -1); handled = true; }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterHotkeys();
            base.OnClosed(e);
            Application.Current.Shutdown();
        }

        private readonly Services.SoundService _hotkeySound = new();

        // Hotkey: the streamer fires redeem slot N themselves (no point cost).
        private void HotkeyRedeem(int index)
        {
            var redeems = Services.SettingsService.LoadChatFeatures().Redeems;
            if (index < 0 || index >= redeems.Count)
            {
                StatusText.Text = $"Hotkey: no redeem in slot {index + 1}.";
                return;
            }
            var r = redeems[index];
            if (!string.IsNullOrWhiteSpace(r.SoundPath))
                _hotkeySound.Play(r.SoundPath, r.Volume);
            Services.OverlayServer.TriggerEffect(r.Effect,
                r.Effect == "custom" && !string.IsNullOrWhiteSpace(r.ImagePath) ? "/fx/" + index : null);
            if (!string.IsNullOrWhiteSpace(r.VideoPath))
                Services.OverlayServer.PlayVideo("/fxvideo/" + index, (int)(Math.Clamp(r.Volume, 0, 1) * 100));
            if (!string.IsNullOrWhiteSpace(r.MorphPreset))
                Services.VoiceMorphService.ActivateByName(r.MorphPreset);
            Services.StreamStatsService.CountRedeem();
            StatusText.Text = $"Hotkey: fired {r.Command} (slot {index + 1}).";
        }

        // A per-counter +/- hotkey fired (single global value; no per-game scoping here).
        private void CounterBump(int index, int delta)
        {
            if (Views.CountersWindow.Current is { } w)
            {
                w.BumpByIndex(index, delta);
                return;
            }
            var list = Services.SettingsService.LoadCounters();
            if (index < 0 || index >= list.Count) return;
            var c = list[index];
            c.SetValue(string.Empty, c.ValueFor(string.Empty) + delta);
            Services.SettingsService.SaveCounters(list);
            Services.OverlayServer.PushCounters(list);
            StatusText.Text = $"Counter: {c.Name} = {c.ValueFor(string.Empty)}.";
        }

        private void HotkeyStopEffects()
        {
            Services.VoiceMorphService.ClearMorph();
            _hotkeySound.StopAll();
            StatusText.Text = "Hotkey: effects stopped, morph ended.";
        }

        #endregion

        #region Menu commands

        private Views.ChatWindow? _chatWindow;

        private Views.ChatWindow OpenChatWindow()
        {
            if (_chatWindow == null)
            {
                _chatWindow = new Views.ChatWindow { Owner = this };
                _chatWindow.Closed += (_, _) => _chatWindow = null;
                _chatWindow.Show();
            }
            return _chatWindow;
        }

        private void Chat_Click(object sender, RoutedEventArgs e) => OpenChatWindow().Activate();

        private Views.ChatFeaturesWindow? _featuresWindow;
        private void ChatFeatures_Click(object sender, RoutedEventArgs e)
        {
            if (_featuresWindow != null) { _featuresWindow.Activate(); return; }
            _featuresWindow = new Views.ChatFeaturesWindow { Owner = this };
            _featuresWindow.Closed += (_, _) => _featuresWindow = null;
            _featuresWindow.Show();
        }

        private Views.GiftAlertsWindow? _giftWindow;
        private void GiftAlerts_Click(object sender, RoutedEventArgs e)
        {
            if (_giftWindow != null) { _giftWindow.Activate(); return; }
            _giftWindow = new Views.GiftAlertsWindow { Owner = this };
            _giftWindow.Closed += (_, _) => _giftWindow = null;
            _giftWindow.Show();
        }

        private Views.GoalsWindow? _goalsWindow;
        private void Goals_Click(object sender, RoutedEventArgs e)
        {
            if (_goalsWindow != null) { _goalsWindow.Activate(); return; }
            _goalsWindow = new Views.GoalsWindow { Owner = this };
            _goalsWindow.Closed += (_, _) => _goalsWindow = null;
            _goalsWindow.Show();
        }

        private Views.CountersWindow? _countersWindow;
        private void Counters_Click(object sender, RoutedEventArgs e)
        {
            if (_countersWindow != null) { _countersWindow.Activate(); return; }
            _countersWindow = new Views.CountersWindow { Owner = this };
            _countersWindow.Closed += (_, _) => _countersWindow = null;
            _countersWindow.Show();
        }

        private Views.TextPanelsWindow? _panelsWindow;
        private void TextPanels_Click(object sender, RoutedEventArgs e)
        {
            if (_panelsWindow != null) { _panelsWindow.Activate(); return; }
            _panelsWindow = new Views.TextPanelsWindow { Owner = this };
            _panelsWindow.Closed += (_, _) => _panelsWindow = null;
            _panelsWindow.Show();
        }

        private Views.VoiceLabWindow? _voiceWindow;
        private void VoiceLab_Click(object sender, RoutedEventArgs e)
        {
            if (_voiceWindow != null) { _voiceWindow.Activate(); return; }
            _voiceWindow = new Views.VoiceLabWindow { Owner = this };
            _voiceWindow.Closed += (_, _) => _voiceWindow = null;
            _voiceWindow.Show();
        }

        private Views.VoiceMorphWindow? _morphWindow;
        private void VoiceMorph_Click(object sender, RoutedEventArgs e)
        {
            if (_morphWindow != null) { _morphWindow.Activate(); return; }
            _morphWindow = new Views.VoiceMorphWindow { Owner = this };
            _morphWindow.Closed += (_, _) => _morphWindow = null;
            _morphWindow.Show();
        }

        private Views.StreamStatsWindow? _statsWindow;
        private void Stats_Click(object sender, RoutedEventArgs e)
        {
            if (_statsWindow != null) { _statsWindow.Activate(); return; }
            _statsWindow = new Views.StreamStatsWindow { Owner = this };
            _statsWindow.Closed += (_, _) => _statsWindow = null;
            _statsWindow.Show();
        }

        private void Overlays_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Services.OverlayService.FolderPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Services.OverlayService.FolderPath) { UseShellExecute = true });
                StatusText.Text = $"Overlay folder opened — add LiveOverlay.html as an OBS browser source (port {Services.OverlayServer.Port}).";
            }
            catch (Exception ex) { StatusText.Text = "Could not open overlay folder: " + ex.Message; }
        }

        private Views.SettingsWindow? _settingsWindow;
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
            _settingsWindow = new Views.SettingsWindow { Owner = this };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        private Views.HelpWindow? _helpWindow;
        private void Help_Click(object sender, RoutedEventArgs e)
        {
            if (_helpWindow != null) { _helpWindow.Activate(); return; }
            _helpWindow = new Views.HelpWindow(_hotkeys,
                onBeginCapture: UnregisterHotkeys,
                onApply: () =>
                {
                    Services.SettingsService.SaveHotkeys(_hotkeys);
                    RegisterHotkeys();
                })
            { Owner = this };
            _helpWindow.Closed += (_, _) => _helpWindow = null;
            _helpWindow.Show();
        }

        #endregion
    }
}
