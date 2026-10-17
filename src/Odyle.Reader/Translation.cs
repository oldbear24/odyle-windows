using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using static Odyle.Core.Localizer;

namespace Odyle.Reader;
// The XAML is written in English. Each window passes its literal texts (titles, text
// blocks, button and toggle captions, headers, tooltips) through T() once after
// InitializeComponent, so Odyle.Core's dictionary stays the only place for translations.
// Bound values and templates are left alone; the code fills those through T() itself.
public static class Translation
{
    public static void Apply(DependencyObject element)
    {
        static bool Bound(DependencyObject target, DependencyProperty property) => BindingOperations.GetBindingExpressionBase(target, property) is not null;
        if (element is Window window) window.Title = T(window.Title);
        else if (element is TextBlock { Inlines.Count: <= 1 } block && block.Text.Length > 0 && !Bound(block, TextBlock.TextProperty)) block.Text = T(block.Text);
        else if (element is ContentControl { Content: string content } control && !Bound(control, ContentControl.ContentProperty)) control.Content = T(content);
        if (element is HeaderedContentControl { Header: string header } headered && !Bound(headered, HeaderedContentControl.HeaderProperty)) headered.Header = T(header);
        if (element is FrameworkElement { ToolTip: string tip } framed && !Bound(framed, FrameworkElement.ToolTipProperty)) framed.ToolTip = T(tip);
        // Screen readers (W#16): a name given in the XAML is translated, and an icon button (a glyph
        // or a symbol, no words) is named by its tooltip's first part ("Settings · account, …").
        if (element is FrameworkElement named)
        {
            var given = AutomationProperties.GetName(named);
            if (given.Length > 0) AutomationProperties.SetName(named, T(given));
            else if (named is ButtonBase { Content: string glyph } && IsGlyph(glyph) && named.ToolTip is string hint) AutomationProperties.SetName(named, NameOf(hint));
        }
        foreach (var child in LogicalTreeHelper.GetChildren(element)) if (child is DependencyObject next) Apply(next);
    }
    // A caption of one or two characters that are not letters or digits: an icon font glyph
    // (Segoe Fluent's private use area), ⚙ or ✕.
    public static bool IsGlyph(string content) => content.Trim() is { Length: > 0 and <= 2 } text && text.All(c => !char.IsLetterOrDigit(c) || c is >= '\uE000' and <= '\uF8FF');
    // The name a tooltip gives a button: its part before the first " · ".
    public static string NameOf(string tooltip) => tooltip.Split(" · ", 2)[0].Trim();
    // A line marked AutomationProperties.LiveSetting tells screen readers it changed (WPF raises
    // nothing by itself); call after setting its text.
    public static void Announce(UIElement line)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        (UIElementAutomationPeer.FromElement(line) ?? UIElementAutomationPeer.CreatePeerForElement(line))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
