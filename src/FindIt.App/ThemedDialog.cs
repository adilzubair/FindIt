using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FindIt.App;

/// <summary>App-owned messages share the library's typography and current palette.</summary>
internal static class ThemedDialog
{
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        => Show(Application.Current?.MainWindow, message, caption, buttons, icon);

    public static MessageBoxResult Show(Window? owner, string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : buttons == MessageBoxButton.YesNo ? MessageBoxResult.None : MessageBoxResult.Cancel;
        var chosen = false;
        var dialog = new Window
        {
            Title = caption, Width = 480, SizeToContent = SizeToContent.Height,
            MinWidth = 360, MaxHeight = Math.Min(720, SystemParameters.WorkArea.Height * .85),
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        if (owner?.IsVisible == true) dialog.Owner = owner;
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "HeadingText");
        layout.Children.Add(heading);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 };
        AutomationProperties.SetName(text, message);
        var content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(120, dialog.MaxHeight - 180) };
        Grid.SetRow(content, 1); layout.Children.Add(content);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        Grid.SetRow(actions, 2); layout.Children.Add(actions);
        void Add(string label, MessageBoxResult value, bool primary = false, bool cancel = false)
        {
            var button = new Button { Content = label, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary, IsCancel = cancel };
            if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            button.Click += (_, _) => { result = value; chosen = true; dialog.Close(); };
            actions.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.OKCancel: Add("_Cancel", MessageBoxResult.Cancel, cancel: true); Add("_OK", MessageBoxResult.OK, primary: true); break;
            case MessageBoxButton.YesNo: Add("_No", MessageBoxResult.No); Add("_Yes", MessageBoxResult.Yes, primary: true); break;
            case MessageBoxButton.YesNoCancel: Add("_Cancel", MessageBoxResult.Cancel, cancel: true); Add("_No", MessageBoxResult.No); Add("_Yes", MessageBoxResult.Yes, primary: true); break;
            default: Add("_OK", MessageBoxResult.OK, primary: true, cancel: true); break;
        }
        dialog.Closing += (_, e) => { if (buttons == MessageBoxButton.YesNo && !chosen) e.Cancel = true; };
        dialog.Content = layout;
        dialog.ShowDialog();
        return result;
    }
}
