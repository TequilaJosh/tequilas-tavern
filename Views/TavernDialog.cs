using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameTracker.Views
{
    /// <summary>
    /// FF1-styled replacement for MessageBox: royal-blue box, white double border,
    /// blocky text, gold-highlight buttons. Drop-in for the MessageBox.Show shapes
    /// the app uses (OK / OKCancel / YesNo).
    /// </summary>
    public static class TavernDialog
    {
        public static MessageBoxResult Show(string text, string title = "Tequilas' Tavern",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
            => Show(null, text, title, buttons, image);

        public static MessageBoxResult Show(Window? owner, string text, string title = "Tequilas' Tavern",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            var result = MessageBoxResult.None;

            var win = new Window
            {
                Title = title,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Topmost = owner == null,
            };
            if (owner != null) win.Owner = owner;

            var blue = new LinearGradientBrush(
                Color.FromRgb(0x14, 0x24, 0xC0), Color.FromRgb(0x0A, 0x16, 0xA0), 90);
            var gold = new SolidColorBrush(Color.FromRgb(0xF8, 0xD8, 0x78));
            var font = new FontFamily("Lucida Console, Consolas");

            Button MakeButton(string label, bool isDefault, MessageBoxResult r)
            {
                var b = new Button
                {
                    Content = label,
                    MinWidth = 96,
                    Margin = new Thickness(6, 0, 6, 0),
                    Padding = new Thickness(14, 7, 14, 7),
                    FontFamily = font,
                    FontSize = 13,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(0x10, 0x1A, 0x4E)),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(1.5),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsDefault = isDefault,
                    IsCancel = r is MessageBoxResult.Cancel or MessageBoxResult.No,
                };
                b.MouseEnter += (_, _) => { b.Foreground = gold; b.BorderBrush = gold; };
                b.MouseLeave += (_, _) => { b.Foreground = Brushes.White; b.BorderBrush = Brushes.White; };
                b.Click += (_, _) => { result = r; win.Close(); };
                return b;
            }

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
            };
            switch (buttons)
            {
                case MessageBoxButton.OKCancel:
                    buttonRow.Children.Add(MakeButton("OK", true, MessageBoxResult.OK));
                    buttonRow.Children.Add(MakeButton("CANCEL", false, MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    buttonRow.Children.Add(MakeButton("YES", true, MessageBoxResult.Yes));
                    buttonRow.Children.Add(MakeButton("NO", false, MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    buttonRow.Children.Add(MakeButton("YES", true, MessageBoxResult.Yes));
                    buttonRow.Children.Add(MakeButton("NO", false, MessageBoxResult.No));
                    buttonRow.Children.Add(MakeButton("CANCEL", false, MessageBoxResult.Cancel));
                    break;
                default:
                    buttonRow.Children.Add(MakeButton("OK", true, MessageBoxResult.OK));
                    break;
            }

            var icon = image switch
            {
                MessageBoxImage.Warning => "⚠",
                MessageBoxImage.Error => "✖",
                MessageBoxImage.Question => "?",
                MessageBoxImage.Information => "◆",
                _ => "",
            };

            var header = new TextBlock
            {
                Text = (icon.Length > 0 ? icon + "  " : "") + title.ToUpperInvariant(),
                Foreground = gold,
                FontFamily = font,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10),
            };
            var body = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontFamily = font,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                LineHeight = 19,
            };

            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            stack.Children.Add(header);
            stack.Children.Add(body);
            stack.Children.Add(buttonRow);

            // FF1 window: white outer border, thin light-blue inner border, blue fill.
            var inner = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x90, 0xE8)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Child = stack,
            };
            var outer = new Border
            {
                Background = blue,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3),
                Child = inner,
            };
            win.Content = outer;

            // Drag anywhere to move (no title bar).
            outer.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };

            win.ShowDialog();
            if (result == MessageBoxResult.None)
                result = buttons switch
                {
                    MessageBoxButton.YesNo => MessageBoxResult.No,
                    MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                    _ => MessageBoxResult.OK,
                };
            return result;
        }
    }
}
