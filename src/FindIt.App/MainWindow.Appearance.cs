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

    private bool _synchronizingAppearance;
    private void ThemeOptionChecked(object sender, RoutedEventArgs e)
    {
        if (_synchronizingAppearance || sender is not RadioButton { Tag: string mode } || !Enum.TryParse<AppearanceMode>(mode, out var choice) || choice == _appearanceMode) return;
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
                ["CanvasBrush"] = "#202020", ["LayerBrush"] = "#272727", ["SurfaceBrush"] = "#2D2D2D", ["SidebarBrush"] = "#202020",
                ["InkBrush"] = "#FFFFFF", ["MutedBrush"] = "#CFCFCF", ["SubtleBrush"] = "#ABABAB", ["BorderBrush"] = "#3A3A3A",
                ["StrongBorderBrush"] = "#555555", ["CheckBorderBrush"] = "#9A9A9A",
                ["AccentBrush"] = "#60CDFF", ["AccentButtonBrush"] = "#60CDFF", ["AccentButtonForegroundBrush"] = "#000000",
                ["AccentTextBrush"] = "#7FD6FF", ["FocusBrush"] = "#FFFFFF", ["TintBrush"] = "#1D3A4A",
                ["HoverBrush"] = "#323232", ["SelectedFillBrush"] = "#363636", ["PreviewBrush"] = "#333333", ["PaperBrush"] = "#323232",
                ["TrackBrush"] = "#505050", ["ScrimBrush"] = "#8C000000",
                ["ReadyBrush"] = "#6CCB5F", ["ReadyTintBrush"] = "#1F3A1D",
                ["WarningBrush"] = "#FCE100", ["WarningTintBrush"] = "#433519", ["ErrorBrush"] = "#FF99A4",
                ["ErrorTintBrush"] = "#442726", ["ShadowBrush"] = "#66000000",
                ["PhotoTintBrush"] = "#173A35", ["PhotoInkBrush"] = "#6FD3C2", ["VideoTintBrush"] = "#2E2547", ["VideoInkBrush"] = "#B9A3F0",
                ["DocumentTintBrush"] = "#1D3047", ["DocumentInkBrush"] = "#8DC1F5", ["AudioTintBrush"] = "#43233A", ["AudioInkBrush"] = "#F0A0CF",
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
                ["CanvasBrush"] = "#F3F3F3", ["LayerBrush"] = "#F9F9F9", ["SurfaceBrush"] = "#FFFFFF", ["SidebarBrush"] = "#F3F3F3",
                ["InkBrush"] = "#1B1B1B", ["MutedBrush"] = "#5C5C5C", ["SubtleBrush"] = "#6B6B6B", ["BorderBrush"] = "#E5E5E5",
                ["StrongBorderBrush"] = "#C9C9C9", ["CheckBorderBrush"] = "#8A8A8A",
                ["AccentBrush"] = "#005FB8", ["AccentButtonBrush"] = "#005FB8", ["AccentButtonForegroundBrush"] = "#FFFFFF",
                ["AccentTextBrush"] = "#005FB8", ["FocusBrush"] = "#1B1B1B", ["TintBrush"] = "#E6EFF8",
                ["HoverBrush"] = "#F0F0F0", ["SelectedFillBrush"] = "#EAEAEA", ["PreviewBrush"] = "#EDEDED", ["PaperBrush"] = "#FFFFFF",
                ["TrackBrush"] = "#D6D6D6", ["ScrimBrush"] = "#52000000",
                ["ReadyBrush"] = "#0F7B0F", ["ReadyTintBrush"] = "#DFF6DD",
                ["WarningBrush"] = "#9D5D00", ["WarningTintBrush"] = "#FFF4CE", ["ErrorBrush"] = "#C42B1C",
                ["ErrorTintBrush"] = "#FDE7E9", ["ShadowBrush"] = "#24000000",
                ["PhotoTintBrush"] = "#DDF3EF", ["PhotoInkBrush"] = "#0B6A5B", ["VideoTintBrush"] = "#ECE6F7", ["VideoInkBrush"] = "#5B3F9E",
                ["DocumentTintBrush"] = "#E3EDF9", ["DocumentInkBrush"] = "#185ABD", ["AudioTintBrush"] = "#F7E4EF", ["AudioInkBrush"] = "#9B2C6E",
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
            foreach (var key in new[] { "CanvasBrush", "LayerBrush", "SurfaceBrush", "SidebarBrush", "PreviewBrush", "PaperBrush", "WarningTintBrush", "ErrorTintBrush", "ReadyTintBrush",
                         "PhotoTintBrush", "VideoTintBrush", "DocumentTintBrush", "AudioTintBrush" }) resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "InkBrush", "MutedBrush", "SubtleBrush", "ReadyBrush", "WarningBrush", "ErrorBrush", "BorderBrush", "StrongBorderBrush", "CheckBorderBrush", "TrackBrush",
                         "PhotoInkBrush", "VideoInkBrush", "DocumentInkBrush", "AudioInkBrush" }) resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "AccentBrush", "AccentTextBrush", "FocusBrush" }) resources[key] = SystemColors.HotTrackBrush;
            resources["AccentButtonBrush"] = SystemColors.HighlightBrush;
            resources["AccentButtonForegroundBrush"] = SystemColors.HighlightTextBrush;
            resources["TintBrush"] = SystemColors.ControlBrush;
            resources["HoverBrush"] = SystemColors.ControlBrush;
            resources["SelectedFillBrush"] = SystemColors.HighlightBrush;
            resources["ScrimBrush"] = Brushes.Transparent;
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
        if (ThemeSystemOption is null) return;
        _synchronizingAppearance = true;
        try { foreach (var option in new[] { ThemeSystemOption, ThemeLightOption, ThemeDarkOption }) option.IsChecked = option.Tag?.ToString() == _appearanceMode.ToString(); }
        finally { _synchronizingAppearance = false; }
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
