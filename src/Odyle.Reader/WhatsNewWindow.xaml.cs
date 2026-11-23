using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Odyle.Core;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;
// What's new (#34): the changelog built into the app (CHANGELOG.md, Odyle.Core.Changelog), newest
// version first, the running one marked. Opened from the card's line after an update and from
// Settings → Updates. In the card's style: each version's number and date over a glass tile with its
// New, Changed and Fixed lines.
public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(ChangeEntry[] entries, string current)
    {
        InitializeComponent(); Translation.Apply(this);
        Brush Resource(string key) => (Brush)FindResource(key);
        Border Tile() => new() { Style = (Style)FindResource("SettingsTile") };
        if (entries.Length == 0)
        {
            var empty = Tile(); empty.Margin = new Thickness(0, 8, 0, 0);
            empty.Child = new TextBlock { Text = T("No changes listed in this build."), Foreground = Resource("Muted") };
            Entries.Children.Add(empty);
        }
        foreach (var entry in entries)
        {
            var head = new DockPanel { Margin = new Thickness(4, 16, 4, 7) };
            var date = new TextBlock { Text = Day(entry.Date), Foreground = Resource("Muted"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(date, Dock.Right); head.Children.Add(date);
            var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new Run(entry.Version));
            if (entry.Version == current) title.Inlines.Add(new Run("  " + T("this version")) { FontSize = 10.5, FontWeight = FontWeights.Normal, Foreground = Resource("Mint") });
            head.Children.Add(title);
            Entries.Children.Add(head);
            var tile = Tile(); var lines = new StackPanel(); tile.Child = lines;
            // The running version's tile is edged in mint, as the card's ring is.
            if (entry.Version == current) tile.BorderBrush = Resource("MintEdge");
            foreach (var group in entry.Groups)
            {
                lines.Children.Add(new TextBlock { Text = GroupName(group.Title).ToUpper(Culture), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Resource("Muted"), Margin = new Thickness(0, lines.Children.Count == 0 ? 0 : 10, 0, 4) });
                foreach (var item in group.Items)
                {
                    var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var bullet = new Ellipse { Width = 5, Height = 5, Fill = Resource("Mint"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 6, 9, 0) };
                    DockPanel.SetDock(bullet, Dock.Left); line.Children.Add(bullet);
                    var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Resource("Text"), LineHeight = 17 };
                    foreach (var (part, bold) in Changelog.Runs(item)) text.Inlines.Add(new Run(part) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
                    line.Children.Add(text);
                    lines.Children.Add(line);
                }
            }
            Entries.Children.Add(tile);
        }
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11 draws a border around the frameless window; Windows 10 does not, so it draws its own.
        if (Environment.OSVersion.Version.Build < 22000) RootBorder.BorderThickness = new Thickness(1);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    public static string GroupName(string title) => title switch { "New" => T("New"), "Changed" => T("Changed"), "Fixed" => T("Fixed"), _ => title };
    private static string Day(string date) => DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? day.ToString("d MMM yyyy", Culture) : "";
    // The changelog built into this build, or "" without it.
    public static string Embedded()
    {
        using var stream = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return "";
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
