using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Odyle.Core;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;
// What's new (#34): the changelog built into the app (CHANGELOG.md, Odyle.Core.Changelog), newest
// version first, the running one marked. Opened from the card's line after an update and from
// Settings → Updates.
public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(ChangeEntry[] entries, string current)
    {
        InitializeComponent(); Translation.Apply(this);
        Brush Resource(string key) => (Brush)FindResource(key);
        if (entries.Length == 0) Entries.Children.Add(new TextBlock { Text = T("No changes listed in this build."), Foreground = Resource("Muted"), Margin = new Thickness(2, 8, 0, 0) });
        foreach (var entry in entries)
        {
            var head = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
            var date = new TextBlock { Text = Day(entry.Date), Foreground = Resource("Faint"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(date, Dock.Right); head.Children.Add(date);
            var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new Run(entry.Version));
            if (entry.Version == current) title.Inlines.Add(new Run("  " + T("this version")) { FontSize = 10.5, FontWeight = FontWeights.Normal, Foreground = Resource("Mint") });
            head.Children.Add(title);
            Entries.Children.Add(head);
            foreach (var group in entry.Groups)
            {
                Entries.Children.Add(new TextBlock { Text = GroupName(group.Title).ToUpper(Culture), Style = (Style)FindResource("Eyebrow"), Margin = new Thickness(2, 8, 0, 3) });
                foreach (var item in group.Items)
                {
                    var line = new DockPanel { Margin = new Thickness(2, 1, 0, 2) };
                    var bullet = new TextBlock { Text = "•", Foreground = Resource("Mint"), Margin = new Thickness(0, 0, 7, 0) };
                    DockPanel.SetDock(bullet, Dock.Left); line.Children.Add(bullet);
                    var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Resource("Text") };
                    foreach (var (part, bold) in Changelog.Runs(item)) text.Inlines.Add(new Run(part) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
                    line.Children.Add(text);
                    Entries.Children.Add(line);
                }
            }
        }
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
