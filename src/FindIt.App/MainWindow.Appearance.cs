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
                ["CanvasBrush"] = "#18191C", ["SurfaceBrush"] = "#1F2023", ["SidebarBrush"] = "#1F2023",
                ["InkBrush"] = "#F8F9FB", ["MutedBrush"] = "#B5B6B8", ["BorderBrush"] = "#2B2C2F", ["CheckBorderBrush"] = "#717681",
                ["AccentBrush"] = "#3297FF", ["AccentButtonBrush"] = "#3297FF", ["AccentButtonForegroundBrush"] = "#091C30",
                ["AccentTextBrush"] = "#69B3FF", ["FocusBrush"] = "#3297FF", ["TintBrush"] = "#1D3249",
                ["HoverBrush"] = "#232427", ["PreviewBrush"] = "#202125", ["ReadyBrush"] = "#7ECBB6",
                ["WarningBrush"] = "#EDBE71", ["WarningTintBrush"] = "#3A3022", ["ErrorBrush"] = "#F59C9C",
                ["ErrorTintBrush"] = "#40272B", ["ShadowBrush"] = "#66000000",
                ["IconFolderBackBrush"] = "#BD8B37", ["IconFolderFrontBrush"] = "#EBC573", ["IconFolderDetailBrush"] = "#FFF4D9",
                ["IconLibraryBackBrush"] = "#316DAA", ["IconLibraryFrontBrush"] = "#80BAFA", ["IconLibraryDetailBrush"] = "#EDF6FF",
                ["IconPeopleBackBrush"] = "#7D65AF", ["IconPeopleFrontBrush"] = "#C2A4EB", ["IconPeopleDetailBrush"] = "#F8F0FF",
                ["IconPhotoBackBrush"] = "#367F81", ["IconPhotoFrontBrush"] = "#7BCDC0", ["IconPhotoDetailBrush"] = "#EDFFF8",
                ["IconVideoBackBrush"] = "#7F63AE", ["IconVideoFrontBrush"] = "#BF9DE7", ["IconVideoDetailBrush"] = "#F9F1FF",
                ["IconDocumentBackBrush"] = "#316DAA", ["IconDocumentFrontBrush"] = "#80BAFA", ["IconDocumentDetailBrush"] = "#EDF6FF",
                ["IconAudioBackBrush"] = "#A76583", ["IconAudioFrontBrush"] = "#E6A4C1", ["IconAudioDetailBrush"] = "#FFF3F8",
                ["IconUtilityBackBrush"] = "#758CA4", ["IconUtilityFrontBrush"] = "#B0C4D8", ["IconUtilityDetailBrush"] = "#1F2023"
            }
            : new Dictionary<string, string>
            {
                ["CanvasBrush"] = "#FFFFFF", ["SurfaceBrush"] = "#FFFFFF", ["SidebarBrush"] = "#F9F9F9",
                ["InkBrush"] = "#2C2F32", ["MutedBrush"] = "#6B6D70", ["BorderBrush"] = "#E6E6E6", ["CheckBorderBrush"] = "#8C939D",
                ["AccentBrush"] = "#0072EF", ["AccentButtonBrush"] = "#0072EF", ["AccentButtonForegroundBrush"] = "#FFFFFF",
                ["AccentTextBrush"] = "#0067DB", ["FocusBrush"] = "#0072EF", ["TintBrush"] = "#E5F1FD",
                ["HoverBrush"] = "#F4F5F5", ["PreviewBrush"] = "#F6F7F8", ["ReadyBrush"] = "#177565",
                ["WarningBrush"] = "#945A0D", ["WarningTintBrush"] = "#FFF3DB", ["ErrorBrush"] = "#B53838",
                ["ErrorTintBrush"] = "#FCECEC", ["ShadowBrush"] = "#24000000",
                ["IconFolderBackBrush"] = "#BA801F", ["IconFolderFrontBrush"] = "#F0BE55", ["IconFolderDetailBrush"] = "#FFF0C7",
                ["IconLibraryBackBrush"] = "#2868BA", ["IconLibraryFrontBrush"] = "#69A4ED", ["IconLibraryDetailBrush"] = "#EAF4FF",
                ["IconPeopleBackBrush"] = "#7358AF", ["IconPeopleFrontBrush"] = "#AB8DDD", ["IconPeopleDetailBrush"] = "#F4EDFF",
                ["IconPhotoBackBrush"] = "#207B80", ["IconPhotoFrontBrush"] = "#5AB4AB", ["IconPhotoDetailBrush"] = "#E4FAF2",
                ["IconVideoBackBrush"] = "#7756AC", ["IconVideoFrontBrush"] = "#AA83D8", ["IconVideoDetailBrush"] = "#F6EDFF",
                ["IconDocumentBackBrush"] = "#2868BA", ["IconDocumentFrontBrush"] = "#69A4ED", ["IconDocumentDetailBrush"] = "#EAF4FF",
                ["IconAudioBackBrush"] = "#A74F75", ["IconAudioFrontBrush"] = "#D988AA", ["IconAudioDetailBrush"] = "#FFF0F6",
                ["IconUtilityBackBrush"] = "#AFBFCE", ["IconUtilityFrontBrush"] = "#52677D", ["IconUtilityDetailBrush"] = "#F8FAFC"
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
            foreach (var key in new[] { "InkBrush", "MutedBrush", "ReadyBrush", "WarningBrush", "ErrorBrush", "BorderBrush", "CheckBorderBrush" }) resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "AccentBrush", "AccentTextBrush", "FocusBrush" }) resources[key] = SystemColors.HotTrackBrush;
            resources["AccentButtonBrush"] = SystemColors.HighlightBrush;
            resources["AccentButtonForegroundBrush"] = SystemColors.HighlightTextBrush;
            resources["TintBrush"] = SystemColors.ControlBrush;
            resources["HoverBrush"] = SystemColors.ControlBrush;
            // The icon families share system colors in high contrast. Their shape
            // and layers remain meaningful without relying on decorative hues.
            foreach (var family in new[] { "Folder", "Library", "People", "Photo", "Video", "Document", "Audio", "Utility" })
            {
                resources[$"Icon{family}BackBrush"] = SystemColors.WindowTextBrush;
                resources[$"Icon{family}FrontBrush"] = SystemColors.HighlightBrush;
                resources[$"Icon{family}DetailBrush"] = SystemColors.HighlightTextBrush;
            }
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
