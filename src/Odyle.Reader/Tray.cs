using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Odyle.Core;
using static Odyle.Core.Localizer;
using Forms = System.Windows.Forms;

namespace Odyle.Reader;
// The tray icon, its menu and its notifications (W#90: moved out of MainWindow, which fills the menu as it opens).
// The icon carries the reader's state (W#17): the card's reader dot in its corner, the same colour (live, listening,
// waiting or stale, a problem), none while the reader is stopped; each variant is drawn once and kept. The menu says
// what the reader does and lists the account's characters; both are filled each time it opens. Every item's action
// runs on the window's thread.
internal sealed class Tray : IDisposable
{
    // What the menu's items do (the window's); Tasks, Pin and Compact get the item's new check state.
    internal sealed record Actions(Action Open, Action Reader, Action<bool> Tasks, Action Opening, Action<bool> Pin, Action<bool> Compact, Action Settings, Func<Task> Quit);
    private readonly Forms.NotifyIcon icon;
    internal readonly Forms.ToolStripMenuItem Status = new("") { Enabled = false }, Reader = new(""), Tasks = new(T("Show tasks")) { CheckOnClick = true }, Characters = new(T("Characters"));
    internal readonly Forms.ToolStripMenuItem Pin = new(T("Stay on top")) { CheckOnClick = true }, Compact = new(T("Compact mode")) { CheckOnClick = true };
    // The last notification, kept for the checks (the tray icon is off there).
    internal (string Title, string Text)? LastNotice;
    private readonly Dictionary<string, System.Drawing.Icon> icons = [];
    private readonly Func<string, System.Windows.Media.Brush> brush;
    private readonly Dispatcher dispatcher;
    private readonly Action open;
    // visible: false in the checks. brush: the window's colour of a dot.
    internal Tray(bool visible, Dispatcher dispatcher, Func<string, System.Windows.Media.Brush> brush, Actions on)
    {
        this.dispatcher = dispatcher; this.brush = brush; open = on.Open;
        icon = new Forms.NotifyIcon { Icon = Drawn(null), Text = T("Odyle · Not connected"), Visible = visible };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add(Status); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("Open Odyle"), null, (_, _) => dispatcher.Invoke(on.Open));
        Reader.Click += (_, _) => dispatcher.Invoke(on.Reader); menu.Items.Add(Reader);
        Tasks.Click += (_, _) => dispatcher.Invoke(() => on.Tasks(Tasks.Checked)); menu.Items.Add(Tasks);
        menu.Items.Add(Characters); menu.Opening += (_, _) => dispatcher.Invoke(on.Opening);
        Pin.Click += (_, _) => dispatcher.Invoke(() => on.Pin(Pin.Checked)); menu.Items.Add(Pin);
        Compact.Click += (_, _) => dispatcher.Invoke(() => on.Compact(Compact.Checked)); menu.Items.Add(Compact);
        menu.Items.Add(T("Settings"), null, (_, _) => dispatcher.Invoke(on.Settings));
        menu.Items.Add(T("Quit"), null, async (_, _) => await dispatcher.InvokeAsync(on.Quit).Task.Unwrap()); icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => dispatcher.Invoke(on.Open); icon.BalloonTipClicked += (_, _) => dispatcher.Invoke(on.Open);
    }
    // The reader's dot (a colour key), or none while it is stopped.
    internal void ShowState(string? dot) => icon.Icon = Drawn(dot);
    // The icon as it is now (the checks compare and save it).
    internal object CurrentIcon => icon.Icon!;
    // The tooltip: Windows keeps 63 characters of it.
    internal void ShowTip(string tip)
    {
        if (tip.Length > 63) tip = tip[..63];
        if (icon.Text != tip) icon.Text = tip;
    }
    // The account's characters as rows of the menu's Characters item; a row opens the window.
    internal void ListCharacters(IReadOnlyCollection<string> rows)
    {
        Characters.DropDownItems.Clear();
        foreach (var row in rows) Characters.DropDownItems.Add(row, null, (_, _) => dispatcher.Invoke(open));
        Characters.Enabled = rows.Count > 0;
    }
    // A tray notification (a toast on Windows 10 and 11); clicking it opens the window.
    internal void Notify(string title, string text)
    {
        LastNotice = (title, text); Log.Info("Notification: " + title);
        // Windows keeps 63 characters of a notification title and 255 of its text.
        if (icon.Visible) icon.ShowBalloonTip(10000, title.Length > 63 ? title[..62] + "…" : title, text.Length > 255 ? text[..254] + "…" : text, Forms.ToolTipIcon.Info);
    }
    public void Dispose() => icon.Dispose();
    private System.Drawing.Icon Drawn(string? dot)
    {
        if (dot is null) return icons.TryGetValue("", out var plain) ? plain : icons[""] = Load();
        if (icons.TryGetValue(dot, out var known)) return known;
        try
        {
            var color = ((System.Windows.Media.SolidColorBrush)brush(dot)).Color;
            using var bitmap = Drawn(null).ToBitmap();
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var size = bitmap.Width * 0.46f; var at = bitmap.Width - size - 0.5f;
                using var ring = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 12, 18, 14));
                using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B));
                g.FillEllipse(ring, at - 2, at - 2, size + 2, size + 2);
                g.FillEllipse(fill, at, at, size - 2, size - 2);
            }
            var handle = bitmap.GetHicon();
            // A copy owns its own handle, so the one GetHicon made can be released right away.
            using (var drawn = System.Drawing.Icon.FromHandle(handle)) icons[dot] = (System.Drawing.Icon)drawn.Clone();
            NativeWindows.ReleaseIcon(handle);
            return icons[dot];
        }
        catch { return Drawn(null); }
    }
    private static System.Drawing.Icon Load()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Odyle;component/Assets/odyle.ico"));
            if (resource is not null) return new System.Drawing.Icon(resource.Stream, 32, 32);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}
