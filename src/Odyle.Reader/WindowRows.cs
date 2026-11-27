using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Odyle.Core;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;
// The rows the window's lists show (W#90: moved out of MainWindow.xaml.cs): records equal when they show the same,
// so a render that changes nothing keeps a list's items (MainWindow.ShowRows).
public sealed record CharacterSharing(string Title, string Rooms);
public sealed record LanguageChoice(string Code, string Name);
// Gap is the room above the row and Pad the room around it (the frames' nesting, GameBands.Nest); Edge is the
// chip's edge in compact mode (variant C's floating chips): blue in a party, violet in a force, else faint.
public sealed record CompactMate(string Name, string Value, string Cubes, Brush Dot, string Hint)
{
    public Thickness Gap { get; init; }
    public Thickness Pad { get; init; }
    public Brush? Edge { get; init; }
}
// Place: where the character was last seen while its player shares their location, "" otherwise; it
// follows the detail with a map pin, in place of the server. Key names the row, which a click opens to show
// its details under it (Open) and closes again; Progress and Bar draw its ring, filled with its natural pool towards
// its cap; ToNext says how far to the next cube; Tag follows the name ("party", "force", or how long ago a stale one was seen) in TagBrush.
public sealed record PartyMate(string Name, string Detail, string Value, string Cubes, Brush Dot, string Hint, string Place = "")
{
    public string Key { get; init; } = "";
    public double Progress { get; init; }
    public Brush? Bar { get; init; }
    public string ToNext { get; init; } = "";
    public string Tag { get; init; } = "";
    public Brush? TagBrush { get; init; }
    public string Toggle { get; init; } = "";
    public bool Open { get; init; }
    public Visibility DetailsVisibility => Open ? Visibility.Visible : Visibility.Collapsed;
    public string PlaceGap => Place.Length > 0 && Detail.Length > 0 ? " · " : "";
    public string Pin => Place.Length > 0 ? "\uE707 " : "";
}
// The players of one party (blue) or force (violet) in the game, framed together (GameBands.Nest):
// its label, player count and icon (the party list shows them), Items its rows and, in a force, its
// parties' frames; Gap is the room around the frame, Pad the room inside it.
public sealed record MateBand(string Label, string Count, string Icon, Brush Accent, Brush Edge, Brush Fill, object[] Items, Thickness Gap, Thickness Pad);
// Summary is the room's party line (RunChip.PartyLine), mint while the party can do a cube. Shown is
// what the list shows: the cards, with the players the game put together framed (Entries).
public sealed record PartyRoom(string Name, PartyMate[] Mates, string Summary = "", Brush? SummaryBrush = null, object[]? Entries = null)
{
    public string Header => Name.ToUpper(Culture);
    public string Online => T("{0} online", Mates.Length);
    // The party line only while others are online: "nobody live" beside "0 online" says nothing new.
    public Visibility SummaryVisibility => Mates.Length > 0 && Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object[] Shown => Entries ?? Mates;
}
// One checklist task as the panel shows it: done within the current reset period or not.
public sealed record TaskItem(UserTask Task, bool Done, string Hint)
{
    public string Id => Task.Id;
    public string Title => Task.Title;
}
// The daily or weekly list; Reset counts down while the window is open.
public sealed class TaskGroup(string cadence, TaskItem[] tasks) : INotifyPropertyChanged
{
    private string reset = "", resetAt = "";
    public string Cadence => cadence;
    public TaskItem[] Tasks => tasks;
    public string Header => cadence == "daily" ? T("DAILY") : T("WEEKLY");
    public string Progress => $"  {tasks.Count(t => t.Done)} / {tasks.Length}";
    public double Percent => tasks.Length == 0 ? 0 : tasks.Count(t => t.Done) * 100.0 / tasks.Length;
    public string Empty => cadence == "daily" ? T("No daily tasks yet · add them on the dashboard.") : T("No weekly tasks yet · add them on the dashboard.");
    public Visibility EmptyVisibility => tasks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Reset { get => reset; set => Set(ref reset, value, nameof(Reset)); }
    public string ResetAt { get => resetAt; set => Set(ref resetAt, value, nameof(ResetAt)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set(ref string field, string value, string name) { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}
