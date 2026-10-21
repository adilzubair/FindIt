using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FindIt.App;

/// <summary>App-owned messages share the library's typography and current palette.</summary>
internal static class ThemedDialog
{
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        => Show(Application.Current?.MainWindow, message, caption, buttons, icon);

    public static MessageBoxResult Show(Window? owner, string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        DialogAction[] actions = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { new DialogAction("_Cancel", MessageBoxResult.Cancel, Cancel: true), new DialogAction("_OK", MessageBoxResult.OK, Primary: true) },
            MessageBoxButton.YesNo => [new("_No", MessageBoxResult.No, Cancel: true), new("_Yes", MessageBoxResult.Yes, Primary: true)],
            MessageBoxButton.YesNoCancel => [new("_Cancel", MessageBoxResult.Cancel, Cancel: true), new("_No", MessageBoxResult.No), new("_Yes", MessageBoxResult.Yes, Primary: true)],
            _ => [new DialogAction("_OK", MessageBoxResult.OK, Primary: true, Cancel: true)]
        };
        var dialog = Create(owner, message, caption, icon, actions, value => result = value);
        dialog.ShowDialog();
        return result;
    }

    public static bool ConfirmDiscard(Window owner, string message, string caption = "Unsaved changes")
    {
        var discard = false;
        var dialog = Create(owner, message, caption, MessageBoxImage.Warning,
            [new("_Keep editing", MessageBoxResult.Cancel, Primary: true, Cancel: true), new("_Discard changes", MessageBoxResult.Yes)],
            value => discard = value == MessageBoxResult.Yes);
        dialog.ShowDialog();
        return discard;
    }

    private sealed record DialogAction(string Label, MessageBoxResult Result, bool Primary = false, bool Cancel = false);

    private static Window Create(Window? owner, string message, string caption, MessageBoxImage icon, IReadOnlyList<DialogAction> actions, Action<MessageBoxResult> completed)
    {
        var availableWidth = Math.Max(280, SystemParameters.WorkArea.Width - 32);
        var dialog = new Window
        {
            Title = caption, Width = Math.Min(480, availableWidth), SizeToContent = SizeToContent.Height,
            MinWidth = Math.Min(360, availableWidth), MaxWidth = availableWidth,
            MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 32),
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        if (owner?.IsVisible == true) dialog.Owner = owner;
        ApplyWindowAppearance(dialog);
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var body = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (icon != MessageBoxImage.None)
        {
            var warning = icon == MessageBoxImage.Warning;
            var error = icon == MessageBoxImage.Error;
            var geometry = warning ? "M9,1 L17,16 1,16 Z M9,6 L9,10 M9,12 L9,13" : error
                ? "M9,0 A9,9 0 1 1 9,18 A9,9 0 1 1 9,0 M6,6 L12,12 M12,6 L6,12"
                : "M9,0 A9,9 0 1 1 9,18 A9,9 0 1 1 9,0 M9,8 L9,13 M9,4 L9,5";
            var symbol = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 20, Height = 20, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0) };
            symbol.SetResourceReference(Shape.StrokeProperty, warning ? "WarningBrush" : error ? "ErrorBrush" : "AccentBrush");
            DockPanel.SetDock(symbol, Dock.Left); heading.Children.Add(symbol);
        }
        var title = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(FrameworkElement.StyleProperty, "HeadingText");
        heading.Children.Add(title); body.Children.Add(heading);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 };
        AutomationProperties.SetName(text, message); body.Children.Add(text);
        var content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(80, dialog.MaxHeight - 130), Focusable = true };
        AutomationProperties.SetName(content, "Message"); layout.Children.Add(content);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Button? defaultButton = null;
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, MinWidth = 80, Margin = new Thickness(8, 4, 0, 0), IsDefault = action.Primary, IsCancel = action.Cancel };
            AutomationProperties.SetName(button, action.Label.Replace("_", ""));
            if (action.Primary) { button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton"); defaultButton = button; }
            button.Click += (_, _) => { completed(action.Result); dialog.Close(); };
            footer.Children.Add(button);
        }
        // Standard message boxes let users copy diagnostic text without retyping paths.
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.C || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            try { Clipboard.SetText($"{caption}\n\n{message}"); e.Handled = true; }
            catch (ExternalException) { /* A busy clipboard does not block dismissing the message. */ }
        };
        dialog.Content = layout;
        dialog.Loaded += (_, _) => defaultButton?.Focus();
        return dialog;
    }

    internal static void ApplyWindowAppearance(Window dialog)
    {
        dialog.SetResourceReference(Window.BackgroundProperty, "CanvasBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "InkBrush");
        dialog.SetResourceReference(Window.FontFamilyProperty, "AppFont");
        dialog.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FindIt.ico"));
        dialog.SourceInitialized += (_, _) =>
        {
            var color = (Application.Current.TryFindResource("CanvasBrush") as SolidColorBrush)?.Color ?? Colors.White;
            var dark = !SystemParameters.HighContrast && (color.R * .299 + color.G * .587 + color.B * .114) < 128 ? 1 : 0;
            var handle = new WindowInteropHelper(dialog).Handle;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) < 0) DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
