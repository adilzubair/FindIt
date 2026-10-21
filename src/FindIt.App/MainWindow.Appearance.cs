using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace FindIt.App;

public partial class MainWindow
{
    private enum AppearanceMode { System, Light, Dark }
    private AppearanceMode _appearanceMode = AppearanceMode.System;
    private bool _appearanceSubscribed;

    private void InitializeAppearance()
    {
        if (!_appearanceSubscribed)
        {
            SourceInitialized += AppearanceSourceInitialized;
            SystemEvents.UserPreferenceChanged += AppearancePreferenceChanged;
            SystemParameters.StaticPropertyChanged += AppearanceSystemParameterChanged;
            _appearanceSubscribed = true;
        }
        ApplyAppearance();
    }

    private void DisposeAppearance()
    {
        if (!_appearanceSubscribed) return;
        SourceInitialized -= AppearanceSourceInitialized;
        SystemEvents.UserPreferenceChanged -= AppearancePreferenceChanged;
        SystemParameters.StaticPropertyChanged -= AppearanceSystemParameterChanged;
        _appearanceSubscribed = false;
    }

    private void AppearanceSourceInitialized(object? sender, EventArgs e) => UpdateNativeFrameAppearance();

    private void AppearancePreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueAppearanceUpdate();
    private void AppearanceSystemParameterChanged(object? sender, PropertyChangedEventArgs e) => QueueAppearanceUpdate();
    private void QueueAppearanceUpdate()
    {
        if (!_appearanceSubscribed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (_appearanceSubscribed) ApplyAppearance(); }));
    }

    private void ThemeClick(object sender, RoutedEventArgs e)
    {
        if (ThemeButton.ContextMenu is not { } menu) return;
        UpdateNativeFrameAppearance();
        SynchronizeAppearanceMenu();
        menu.PlacementTarget = ThemeButton;
        menu.Placement = PlacementMode.Right;
        menu.IsOpen = true;
    }

    private void ThemeMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode } || !Enum.TryParse<AppearanceMode>(mode, out var choice)) return;
        _appearanceMode = choice;
        ApplyAppearance();
    }

    // Capture tooling uses the same appearance path as a session override.
    public void SetDarkTheme(bool dark)
    {
        _appearanceMode = dark ? AppearanceMode.Dark : AppearanceMode.Light;
        ApplyAppearance();
    }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int preference && preference == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private void ApplyAppearance()
    {
        _dark = _appearanceMode == AppearanceMode.Dark || (_appearanceMode == AppearanceMode.System && WindowsUsesDarkTheme());
        var colors = _dark
            ? new Dictionary<string, string>
            {
                ["CanvasBrush"] = "#16181D", ["SurfaceBrush"] = "#1D2026", ["SidebarBrush"] = "#191C22",
                ["InkBrush"] = "#F0F2F5", ["MutedBrush"] = "#A5ADB8", ["BorderBrush"] = "#30353F",
                ["AccentBrush"] = "#8CA9FF", ["AccentButtonBrush"] = "#8CA9FF", ["AccentButtonForegroundBrush"] = "#17213C",
                ["AccentTextBrush"] = "#A9BFFF", ["FocusBrush"] = "#8CA9FF", ["TintBrush"] = "#28334C",
                ["HoverBrush"] = "#282D36", ["PreviewBrush"] = "#121419", ["ReadyBrush"] = "#7ECBB6",
                ["WarningBrush"] = "#EDBE71", ["WarningTintBrush"] = "#3A3022", ["ErrorBrush"] = "#F59C9C",
                ["ErrorTintBrush"] = "#40272B", ["ShadowBrush"] = "#66000000"
            }
            : new Dictionary<string, string>
            {
                ["CanvasBrush"] = "#F6F7F9", ["SurfaceBrush"] = "#FFFFFF", ["SidebarBrush"] = "#F0F2F5",
                ["InkBrush"] = "#17212D", ["MutedBrush"] = "#667180", ["BorderBrush"] = "#E3E7ED",
                ["AccentBrush"] = "#3D65D8", ["AccentButtonBrush"] = "#3D65D8", ["AccentButtonForegroundBrush"] = "#FFFFFF",
                ["AccentTextBrush"] = "#3D65D8", ["FocusBrush"] = "#3D65D8", ["TintBrush"] = "#E9EEFC",
                ["HoverBrush"] = "#EAEDF2", ["PreviewBrush"] = "#ECEFF3", ["ReadyBrush"] = "#177565",
                ["WarningBrush"] = "#945A0D", ["WarningTintBrush"] = "#FFF3DB", ["ErrorBrush"] = "#B53838",
                ["ErrorTintBrush"] = "#FCECEC", ["ShadowBrush"] = "#1F17212D"
            };
        foreach (var (key, hex) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
        if (SystemParameters.HighContrast)
        {
            var resources = Application.Current.Resources;
            foreach (var key in new[] { "CanvasBrush", "SurfaceBrush", "SidebarBrush", "PreviewBrush", "WarningTintBrush", "ErrorTintBrush" }) resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "InkBrush", "MutedBrush", "ReadyBrush", "WarningBrush", "ErrorBrush", "BorderBrush" }) resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "AccentBrush", "AccentTextBrush", "FocusBrush" }) resources[key] = SystemColors.HotTrackBrush;
            resources["AccentButtonBrush"] = SystemColors.HighlightBrush;
            resources["AccentButtonForegroundBrush"] = SystemColors.HighlightTextBrush;
            resources["TintBrush"] = SystemColors.ControlBrush;
            resources["HoverBrush"] = SystemColors.ControlBrush;
        }
        UpdateNativeFrameAppearance();
        SynchronizeAppearanceMenu();
    }

    private void SynchronizeAppearanceMenu()
    {
        if (ThemeButton?.ContextMenu is not { } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsChecked = item.Tag?.ToString() == _appearanceMode.ToString();
        ThemeButton.ToolTip = $"Appearance: {_appearanceMode}";
    }

    private void UpdateNativeFrameAppearance()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var darkFrame = _dark && !SystemParameters.HighContrast ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref darkFrame, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
