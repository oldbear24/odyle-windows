using System.Windows;
using System.Windows.Controls;
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
        foreach (var child in LogicalTreeHelper.GetChildren(element)) if (child is DependencyObject next) Apply(next);
    }
}
