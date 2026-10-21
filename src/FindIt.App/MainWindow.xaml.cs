using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FindIt.Shared;
using FindIt.Client;
using Microsoft.Win32;

namespace FindIt.App;

public partial class MainWindow : Window
{
    private IArchiveBackend? _backend;
    private readonly string _root;
    private readonly ThumbnailCache _thumbnails;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private ArchiveStatus _status = new();
    private IReadOnlyList<PersonRecord> _people = [];
    private CancellationTokenSource? _searchCancellation, _previewCancellation, _prepareCancellation;
    private CancellationTokenSource? _updateCancellation;
    private bool _updating;
    private Task? _preparationTask;
    private SearchResult? _selectedResult;
    private PreviewResult? _preview;
    private PersonRecord? _editingPerson;
    private string[] _references = [];
    private readonly ObservableCollection<string> _preparationFolders = [];
    private readonly List<string?> _pageCursors = [null];
    private readonly Dictionary<int, SearchResponse> _pages = [];
    private readonly Queue<int> _pageLru = [];
    private int _page, _columns;
    private long? _searchGeneration;
    private bool _initialized, _searching, _statusPolling, _dark, _closing, _closed, _ejectRequested;
    private bool _noticePersistent;
    private PreviewPlayerClient? _player;
    private readonly SemaphoreSlim _playbackGate = new(1);
    private readonly Dictionary<ResultTile, CancellationTokenSource> _thumbnailRequests = [];
    public ObservableCollection<ResultRow> Rows { get; } = [];
    public ObservableCollection<ResultTile> CurrentTiles { get; } = [];

    public MainWindow(IArchiveBackend? backend, string? rootPath = null)
    {
        _backend = backend;
        _root = Path.GetFullPath(rootPath ?? backend?.RootPath ?? AppContext.BaseDirectory);
        _thumbnails = new(_root);
        InitializeComponent();
        SetDarkTheme(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int preference && preference == 0);
        DataContext = this;
        PreparationFolders.ItemsSource = _preparationFolders;
        AutomationProperties.SetLiveSetting(NoticeText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(ResultCount, AutomationLiveSetting.Polite);
        DriveLocation.Text = _root;
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        KindFilter.SelectedIndex = DateFilter.SelectedIndex = 0;
        FolderFilter.ItemsSource = new[] { new FolderChoice("All folders", null) }; FolderFilter.SelectedIndex = 0;
        PersonFilter.ItemsSource = new[] { new PersonChip(Guid.Empty, "Anyone") }; PersonFilter.SelectedIndex = 0;
        Navigate("search");
        if (_backend is null) { FooterStatus.Text = "Opening archive…"; StartPreparationButton.IsEnabled = false; return; }
        StartPreparationButton.IsEnabled = true;
        await RefreshStatusAsync();
        await LoadPeopleAsync();
        _initialized = true;
        _statusTimer.Start();
        if (_status.FileCount > 0) await SearchAsync(true);
        QueryBox.Focus();
    }

    public async Task AttachBackendAsync(IArchiveBackend backend)
    {
        if (_closing || _closed) { await backend.DisposeAsync(); return; }
        _backend = backend;
        await InitializeAsync();
    }
    public void ShowRenderPage(string page) { Navigate(page); FooterStatus.Text = "UI preview · no archive opened"; }
    public async Task RenderArchiveAsync(string? query, bool previewFirst, string? kind = null, bool selectPerson = false)
    {
        _initialized = false;
        KindFilter.SelectedIndex = kind?.ToLowerInvariant() switch { "photo" or "image" => 1, "video" => 2, "document" => 3, "audio" => 4, _ => 0 };
        _initialized = true;
        QueryBox.Text = query ?? "";
        await SearchAsync(true);
        if (previewFirst && CurrentTiles.FirstOrDefault() is ResultTile first) await SelectResultAsync(first.Result);
        if (selectPerson && _people.FirstOrDefault() is PersonRecord person) PeopleList.SelectedItem = person;
        FooterStatus.Text = $"UI capture · {_status.FileCount:N0} real archive files";
    }
    public async Task PrepareCaptureAsync()
    {
        if (_preview?.Kind != FileKind.Video) return;
        // RenderTargetBitmap cannot capture a foreign HWND. Show the actual indexed frame in captures.
        var frame = _preview.Moments.FirstOrDefault(m => m.StartMs == _preview.StartMs)?.Thumbnail ?? _selectedResult?.Thumbnail;
        PreviewImage.Source = await _thumbnails.ReadAsync(frame, CancellationToken.None);
        VideoPlayer.Visibility = Visibility.Collapsed;
        FooterStatus.Text += " · Video surface shown as its indexed frame";
    }
    private void Navigate(string page)
    {
        SearchPage.Visibility = page == "search" ? Visibility.Visible : Visibility.Collapsed;
        PeoplePage.Visibility = page == "people" ? Visibility.Visible : Visibility.Collapsed;
        FoldersPage.Visibility = page == "folders" ? Visibility.Visible : Visibility.Collapsed;
        PreparePage.Visibility = page == "prepare" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, name) in new[] { (SearchNav, "search"), (PeopleNav, "people"), (FoldersNav, "folders"), (PrepareNav, "prepare") })
        {
            button.SetResourceReference(BackgroundProperty, page == name ? "TintBrush" : "CanvasBrush");
            button.SetResourceReference(ForegroundProperty, page == name ? "AccentBrush" : "InkBrush");
            button.FontWeight = page == name ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (page != "search") StopPlayback();
    }
    private void SearchNavClick(object sender, RoutedEventArgs e) { Navigate("search"); QueryBox.Focus(); }
    private async void PeopleNavClick(object sender, RoutedEventArgs e) { Navigate("people"); await LoadPeopleAsync(); }
    private void FoldersNavClick(object sender, RoutedEventArgs e) => Navigate("folders");
    private void PrepareNavClick(object sender, RoutedEventArgs e) => Navigate("prepare");

    private async Task RefreshStatusAsync()
    {
        if (_backend is null || _statusPolling || _closing) return;
        _statusPolling = true;
        try
        {
            var previousLibrary = _status.LibraryId;
            _status = Directory.Exists(_root) ? await _backend.GetStatusAsync() : _status with { Connected = false, StatusDetail = "Drive disconnected." };
            if (previousLibrary != Guid.Empty && _status.LibraryId != Guid.Empty && previousLibrary != _status.LibraryId)
            {
                _searchCancellation?.Cancel(); ClosePreview(); CurrentTiles.Clear(); Rows.Clear(); _searchGeneration = null;
                _pages.Clear(); _pageCursors.Clear(); _pageCursors.Add(null); _page = 0;
                Notice("A different archive is connected. Run your search to see this drive's files.");
            }
            DriveName.Text = _status.DisplayName;
            ConnectionLabel.Text = !_status.Connected ? "Drive disconnected" : _status.IsPreparing ? "Preparing" : _status.FileCount > 0 ? "Ready to search" : "Not prepared yet";
            ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _status.Connected ? "ReadyBrush" : "MutedBrush");
            FooterStatus.Text = !_status.Connected ? "Reconnect this drive to continue." : $"{_status.FileCount:N0} files · {_status.SegmentCount:N0} searchable moments · {FormatBytes(_status.TotalBytes)}";
            FolderList.ItemsSource = _status.Folders;
            CapabilitiesList.ItemsSource = _status.Capabilities;
            IssuesList.ItemsSource = _status.Issues;
            IssuesHeading.Text = _status.Issues.Count == 0 ? "No files need attention" : $"{_status.Issues.Count:N0} files needing attention";
            var selectedFolder = (FolderFilter.SelectedItem as FolderChoice)?.Path;
            _initialized = false;
            var choices = new[] { new FolderChoice("All folders", null) }.Concat(_status.Folders.Select(f => new FolderChoice(f.RelativePath, f.RelativePath))).ToArray();
            FolderFilter.ItemsSource = choices; FolderFilter.SelectedItem = choices.FirstOrDefault(f => f.Path == selectedFolder) ?? choices[0];
            _initialized = true;
            if (!_status.Connected)
            {
                StopPlayback(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel();
                Notice("Drive disconnected. Reconnect the same drive to continue. Your query is preserved.");
            }
            else if (_status.IsPreparing) Notice("Preparation is running. Search currently prepared files while the rest are processed.", false);
            else if (_status.FileCount > 0 && !_status.SemanticAvailable) Notice(_status.StatusDetail, false);
            else if (!_searching && string.IsNullOrWhiteSpace(QueryBox.Text)) HideNotice();
            if (CurrentTiles.Count == 0) UpdateEmptyState();
            ApplyResponsiveLayout();
        }
        catch (Exception ex)
        {
            if (!Directory.Exists(_root))
            {
                _status = _status with { Connected = false };
                ConnectionLabel.Text = "Drive disconnected"; ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "MutedBrush");
                FooterStatus.Text = "Reconnect this drive to continue."; StopPlayback(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel();
            }
            Notice($"Archive unavailable: {ex.Message}");
        }
        finally { _statusPolling = false; }
    }

    private async Task LoadPeopleAsync()
    {
        if (_backend is null) return;
        try
        {
            _people = await _backend.GetPeopleAsync();
            PeopleList.ItemsSource = _people;
            PeopleEmpty.Visibility = _people.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var selected = (PersonFilter.SelectedItem as PersonChip)?.PersonId;
            var chips = new[] { new PersonChip(Guid.Empty, "Anyone") }.Concat(_people.Select(p => new PersonChip(p.Id, p.Name))).ToArray();
            var wasInitialized = _initialized; _initialized = false;
            PersonFilter.ItemsSource = chips; PersonFilter.SelectedItem = chips.FirstOrDefault(p => p.PersonId == selected) ?? chips[0];
            _initialized = wasInitialized;
        }
        catch (Exception ex) { Notice($"Could not read people: {ex.Message}"); }
    }

    private SearchFilters ReadFilters()
    {
        DateTimeOffset? after = DateFilter.SelectedIndex switch { 1 => DateTimeOffset.Now.AddDays(-30), 2 => new DateTimeOffset(new DateTime(DateTime.Now.Year, 1, 1), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)), _ => null };
        var person = (PersonFilter.SelectedItem as PersonChip)?.PersonId;
        if (DateFilter.SelectedIndex == 3 && AfterDate.SelectedDate is DateTime afterDate) after = new DateTimeOffset(afterDate, TimeZoneInfo.Local.GetUtcOffset(afterDate));
        DateTimeOffset? before = DateFilter.SelectedIndex == 3 && BeforeDate.SelectedDate is DateTime beforeDate ? new DateTimeOffset(beforeDate.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(beforeDate)) : null;
        return new() { Kind = KindFilter.SelectedIndex switch { 1 => FileKind.Image, 2 => FileKind.Video, 3 => FileKind.Document, 4 => FileKind.Audio, _ => null }, AfterUtc = after?.ToUniversalTime(), BeforeUtc = before?.ToUniversalTime(), Folder = (FolderFilter.SelectedItem as FolderChoice)?.Path, PersonIds = person is not null && person != Guid.Empty ? [person.Value] : [], ExactOnly = ExactFilter.IsChecked == true };
    }

    private async Task SearchAsync(bool reset, int? targetPage = null)
    {
        if (_backend is null || _closing) return;
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = new();
        if (reset) { _page = 0; _pageCursors.Clear(); _pageCursors.Add(null); _pages.Clear(); _pageLru.Clear(); _searchGeneration = null; ClosePreview(); }
        if (targetPage is int page) _page = page;
        _searching = true; ResultCount.Text = "Searching…";
        PreviousPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        try
        {
            if (!_pages.TryGetValue(_page, out var response))
            {
                response = await _backend.SearchAsync(new() { Query = QueryBox.Text.Trim(), Filters = ReadFilters(), Limit = 256, Cursor = _pageCursors.ElementAtOrDefault(_page), GenerationId = _searchGeneration }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _searchGeneration = response.GenerationId;
                _pages[_page] = response; _pageLru.Enqueue(_page);
                while (_pages.Count > 8 && _pageLru.TryDequeue(out var oldest)) _pages.Remove(oldest);
                if (response.NextCursor is not null && _pageCursors.Count == _page + 1) _pageCursors.Add(response.NextCursor);
            }
            CurrentTiles.Clear();
            foreach (var result in response.Results) CurrentTiles.Add(new ResultTile { Result = result });
            ArrangeRows(true);
            EmptyState.Visibility = CurrentTiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (CurrentTiles.Count == 0) UpdateEmptyState();
            var first = _page * 256 + 1; var last = _page * 256 + CurrentTiles.Count;
            ResultCount.Text = CurrentTiles.Count == 0 ? "No results" : $"{first:N0}–{last:N0}{(response.TotalCount is long total ? $" of {total:N0}" : "")} · {response.ElapsedMilliseconds:N0} ms{(response.SemanticUsed ? " · Semantic search" : "")}";
            PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = response.NextCursor is not null;
            if (!string.IsNullOrWhiteSpace(response.Notice)) Notice(response.Notice); else if (!_status.IsPreparing) HideNotice(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) { Notice($"Search could not finish: {ex.Message}"); ResultCount.Text = "Search interrupted. Your query is preserved."; } }
        finally { if (ReferenceEquals(cancellation, _searchCancellation)) _searching = false; }
    }

    private async void TileLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResultTile tile || tile.Thumbnail is not null || _thumbnailRequests.ContainsKey(tile)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_searchCancellation?.Token ?? CancellationToken.None);
        _thumbnailRequests[tile] = cancellation;
        try { tile.Thumbnail = await _thumbnails.ReadAsync(tile.Result.Thumbnail, cancellation.Token); }
        catch (OperationCanceledException) { }
        finally { if (_thumbnailRequests.TryGetValue(tile, out var active) && ReferenceEquals(active, cancellation)) _thumbnailRequests.Remove(tile); cancellation.Dispose(); }
    }
    private void TileUnloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResultTile tile) return;
        if (_thumbnailRequests.TryGetValue(tile, out var request)) request.Cancel();
        tile.Thumbnail = null;
    }

    private async Task StopPlaybackAsync()
    {
        await _playbackGate.WaitAsync();
        try { if (_player is not null) await _player.StopAsync(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { _playbackGate.Release(); }
    }

    private async Task DisposePlaybackAsync()
    {
        await _playbackGate.WaitAsync();
        try
        {
            if (_player is not null) { await _player.DisposeAsync(); _player = null; }
        }
        finally { _playbackGate.Release(); }
    }

    private void ArrangeRows(bool force = false)
    {
        var scroll = VisualChildren<ScrollViewer>(Gallery).FirstOrDefault();
        var available = Math.Max(160, (scroll is { ViewportWidth: > 0 } ? scroll.ViewportWidth : Gallery.ActualWidth - SystemParameters.VerticalScrollBarWidth) - 8);
        var count = Math.Max(1, (int)Math.Floor(available / 200));
        var width = Math.Floor(available / count) - 12;
        if (!force && count == _columns) { foreach (var tile in CurrentTiles) tile.TileWidth = width; return; }
        var previousColumns = Math.Max(1, _columns);
        var anchorIndex = (int)Math.Floor((scroll?.VerticalOffset ?? 0) / 211) * previousColumns;
        _columns = count;
        Rows.Clear();
        foreach (var tile in CurrentTiles) tile.TileWidth = width;
        for (var index = 0; index < CurrentTiles.Count; index += count) Rows.Add(new(CurrentTiles.Skip(index).Take(count).ToArray()));
        if (scroll is not null && anchorIndex > 0) Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset((anchorIndex / count) * 211), DispatcherPriority.Loaded);
    }
    private void ResultsSizeChanged(object sender, SizeChangedEventArgs e) { ApplyResponsiveLayout(); ArrangeRows(); }
    private void ApplyResponsiveLayout()
    {
        if (SearchTitle is null) return;
        var compact = ActualHeight < 820 && _status.FileCount > 0;
        var shortPreview = ActualHeight < 650 && _preview is not null;
        var unprepared = _status.FileCount == 0;
        SearchTitle.FontSize = compact ? 22 : 27;
        SearchTitle.Visibility = shortPreview ? Visibility.Collapsed : Visibility.Visible;
        SearchHeadingRow.Visibility = PaginationBar.Visibility = unprepared ? Visibility.Collapsed : Visibility.Visible;
        QueryToolsPanel.Visibility = unprepared || shortPreview ? Visibility.Collapsed : Visibility.Visible;
        var shortWindow = ActualHeight < 650;
        EmptyIcon.Width = EmptyIcon.Height = shortWindow ? 44 : 72;
        EmptyTitle.FontSize = shortWindow ? 20 : 22;
        EmptyTitle.Margin = shortWindow ? new Thickness(0, 12, 0, 8) : new Thickness(0, 22, 0, 9);
        EmptyDescription.FontSize = shortWindow ? 12 : 14;
        EmptyDescription.LineHeight = shortWindow ? 20 : 23;
        EmptyAction.Margin = new Thickness(0, shortWindow ? 16 : 23, 0, 0);
        PortableHint.Visibility = ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        DriveLocation.MaxWidth = Math.Max(120, Math.Min(550, ActualWidth - 690));
        foreach (var tile in CurrentTiles) tile.ThumbnailHeight = ActualHeight < 650 ? 72 : 148;
        SearchSubtitle.Visibility = QueryHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        QueryToolsPanel.Margin = compact ? new Thickness(0, 14, 0, 12) : new Thickness(0, 22, 0, 19);
        QueryBox.MinHeight = compact ? 44 : 52; QueryBox.Padding = compact ? new Thickness(16, 8, 16, 8) : new Thickness(16, 12, 16, 12);
        if (_preview?.Kind is FileKind.Video or FileKind.Audio)
        {
            PreviewMediaRow.Height = new GridLength(shortPreview ? 90 : compact ? 120 : 160);
            MomentsHeading.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var image in VisualChildren<Image>(MomentsList)) if (image.DataContext is SearchMoment moment) { image.Visibility = moment.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible; image.Height = shortPreview ? 30 : compact ? 36 : 70; }
        }
    }
    private void PreviewResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => ArrangeRows(true);
    private async void SearchClick(object sender, RoutedEventArgs e) => await SearchAsync(true);
    private async void QueryKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(true); } }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e) { if (DateRangePanel is not null) DateRangePanel.Visibility = DateFilter.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed; if (_initialized) await SearchAsync(true); }
    private async void DateRangeChanged(object? sender, SelectionChangedEventArgs e) { if (_initialized && DateFilter.SelectedIndex == 3) await SearchAsync(true); }
    private async void ExactChanged(object sender, RoutedEventArgs e) { if (_initialized) await SearchAsync(true); }
    private async void PreviousClick(object sender, RoutedEventArgs e) { if (_page > 0) await SearchAsync(false, _page - 1); }
    private async void NextClick(object sender, RoutedEventArgs e) { if (_pageCursors.Count > _page + 1) await SearchAsync(false, _page + 1); }
    private void GridViewClick(object sender, RoutedEventArgs e) { Gallery.Visibility = Visibility.Visible; MetadataList.Visibility = Visibility.Collapsed; ArrangeRows(true); }
    private void ListViewClick(object sender, RoutedEventArgs e) { Gallery.Visibility = Visibility.Collapsed; MetadataList.Visibility = Visibility.Visible; }
    private async void ResultClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ResultTile tile) await SelectResultAsync(tile.Result); }
    private async void ResultSelectionChanged(object sender, SelectionChangedEventArgs e) { if (MetadataList.SelectedItem is ResultTile tile) await SelectResultAsync(tile.Result); }
    private void ListDoubleClick(object sender, MouseButtonEventArgs e) { if (_selectedResult is not null) OpenOriginal(); }

    private void UpdateEmptyState()
    {
        var prepared = _status.FileCount > 0;
        EmptyTitle.Text = !_status.Connected ? "Your drive is disconnected" : prepared ? "No matches yet" : "Your archive, ready to discover";
        EmptyDescription.Text = !_status.Connected ? "Reconnect this drive to pick up where you left off." : prepared ? "Try a broader description, remove a filter, or search for words you remember." : "Prepare your photos, videos and documents on a powerful PC. Then bring this drive anywhere and search offline.";
        EmptyAction.Content = prepared ? "Clear search and filters" : "Prepare this drive";
        EmptyAction.IsEnabled = _status.Connected;
        EmptyState.Visibility = Visibility.Visible;
    }
    private async void EmptyActionClick(object sender, RoutedEventArgs e)
    {
        if (_status.FileCount == 0) { Navigate("prepare"); return; }
        _initialized = false; QueryBox.Clear(); KindFilter.SelectedIndex = DateFilter.SelectedIndex = FolderFilter.SelectedIndex = PersonFilter.SelectedIndex = 0; ExactFilter.IsChecked = false; _initialized = true; await SearchAsync(true);
    }

    private async Task SelectResultAsync(SearchResult result, long? segmentId = null)
    {
        if (_backend is null) return;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose();
        var cancellation = _previewCancellation = new();
        _selectedResult = result; StopPlayback();
        PreviewName.Text = result.Name; PreviewPath.Text = result.RelativePath; PreviewText.Text = result.Snippet;
        PreviewMediaRow.Height = new GridLength(200);
        PreviewImage.Source = null; PreviewPlaceholder.Text = "Loading preview…"; PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPanel.Visibility = Visibility.Visible; PreviewColumn.Width = new GridLength(Math.Min(380, ActualWidth * .32)); SplitterColumn.Width = new GridLength(5);
        PossibleMatchBanner.Visibility = result.MatchCategory == MatchCategory.PossiblePersonAction ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var preview = await _backend.GetPreviewAsync(new(result.FileId, segmentId), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); _preview = preview;
            PreviewText.Text = string.IsNullOrWhiteSpace(preview.Text) ? result.Snippet : preview.Text;
            PreviewText.Visibility = string.IsNullOrWhiteSpace(PreviewText.Text) ? Visibility.Collapsed : Visibility.Visible;
            var moments = preview.Moments.Count > 0 ? preview.Moments : result.Moments;
            var showMoments = moments.Count > 1 || result.Kind is FileKind.Video or FileKind.Audio;
            MomentsList.ItemsSource = showMoments ? moments : [];
            MomentsHeading.Visibility = showMoments ? Visibility.Visible : Visibility.Collapsed;
            if (preview.Kind == FileKind.Image || preview.Kind == FileKind.Document)
            {
                var bitmap = await LoadImageFileAsync(preview.PreviewPath ?? (preview.Kind == FileKind.Image ? preview.OriginalPath : null), cancellation.Token);
                if (bitmap is null) bitmap = await _thumbnails.ReadAsync(result.Thumbnail, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested(); PreviewImage.Source = bitmap; PreviewPlaceholder.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
                PreviewPlaceholder.Text = preview.Kind == FileKind.Document ? "Document text below" : "No image preview available";
                if (preview.Kind == FileKind.Document && bitmap is null) PreviewMediaRow.Height = new GridLength(0);
            }
            else if (preview.Kind is FileKind.Video or FileKind.Audio) await OpenPlaybackAsync(preview, cancellation.Token);
            else PreviewPlaceholder.Text = "Open the original to view this file";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PreviewPlaceholder.Text = "Preview unavailable"; Notice($"Could not load preview: {ex.Message}"); }
        ApplyResponsiveLayout();
        ArrangeRows(true);
    }

    private static async Task<BitmapSource?> LoadImageFileAsync(string? path, CancellationToken token)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1200; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return (BitmapSource)bitmap;
            }, token);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException or FormatException) { return null; }
    }

    private async Task OpenPlaybackAsync(PreviewResult preview, CancellationToken token)
    {
        await _playbackGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            VideoPlayer.Visibility = Visibility.Visible; VideoControls.Visibility = Visibility.Visible; PreviewPlaceholder.Visibility = Visibility.Collapsed;
            VideoPlayer.UpdateLayout();
            _player ??= new PreviewPlayerClient(_root, VideoPlayer.WindowHandle);
            PreviewTime.Text = FormatTime(preview.StartMs ?? 0);
            await _player.PlayAsync(preview.OriginalPath, preview.StartMs ?? 0, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            VideoPlayer.Visibility = VideoControls.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Text = $"Video runtime unavailable: {ex.Message}"; PreviewPlaceholder.Visibility = Visibility.Visible;
            if (_player is not null) { await _player.DisposeAsync(); _player = null; }
        }
        finally { _playbackGate.Release(); }
    }
    private void StopPlayback() { _ = StopPlaybackAsync(); VideoPlayer.Visibility = Visibility.Collapsed; VideoControls.Visibility = Visibility.Collapsed; }
    private async void PlayPauseClick(object sender, RoutedEventArgs e) { if (_player is null) return; try { await _player.PauseAsync(); } catch (Exception ex) { Notice($"Playback interrupted: {ex.Message}"); } }
    private async void MomentClick(object sender, RoutedEventArgs e) { if (_selectedResult is not null && (sender as FrameworkElement)?.Tag is SearchMoment moment) await SelectResultAsync(_selectedResult, moment.SegmentId); }
    private void ClosePreviewClick(object sender, RoutedEventArgs e) => ClosePreview();
    private void ClosePreview() { _previewCancellation?.Cancel(); StopPlayback(); PreviewPanel.Visibility = Visibility.Collapsed; PreviewColumn.Width = new GridLength(0); SplitterColumn.Width = new GridLength(0); _preview = null; _selectedResult = null; ApplyResponsiveLayout(); ArrangeRows(true); }
    private void OpenOriginalClick(object sender, RoutedEventArgs e) => OpenOriginal();
    private void OpenOriginal()
    {
        try { var path = _preview?.OriginalPath ?? (_selectedResult is null ? null : SafePath(_selectedResult.RelativePath)); if (path is not null) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Notice($"Could not open original: {ex.Message}"); }
    }
    private void ShowInFolderClick(object sender, RoutedEventArgs e)
    {
        try { var path = _preview?.OriginalPath ?? (_selectedResult is null ? null : SafePath(_selectedResult.RelativePath)); if (path is not null) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"/select,\"{path}\"" }); }
        catch (Exception ex) { Notice($"Could not open folder: {ex.Message}"); }
    }

    private void AddPersonClick(object sender, RoutedEventArgs e) { _editingPerson = null; PeopleList.SelectedItem = null; PersonHeading.Text = "Add a person"; PersonName.Clear(); PersonAliases.Clear(); _references = []; ReferenceCount.Text = "No new photos selected"; PersonObservations.Text = ""; SearchPersonButton.IsEnabled = ReviewFacesButton.IsEnabled = MergePersonButton.IsEnabled = false; PersonName.Focus(); }
    private void PersonSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is not PersonRecord person) return;
        _editingPerson = person; PersonHeading.Text = "Edit person"; PersonName.Text = person.Name; PersonAliases.Text = string.Join(", ", person.Aliases); _references = []; ReferenceCount.Text = "No new photos selected";
        PersonObservations.Text = $"{person.ObservationCount:N0} face observations. Review uncertain matches before relying on them.";
        SearchPersonButton.IsEnabled = ReviewFacesButton.IsEnabled = MergePersonButton.IsEnabled = true;
    }
    private void ReferencePhotosClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose reference photos on this drive", InitialDirectory = _root, Multiselect = true, AddToRecent = false, RestoreDirectory = true, Filter = "Photos|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.heic;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog(this) == true) { _references = dialog.FileNames; ReferenceCount.Text = $"{_references.Length} photo{(_references.Length == 1 ? "" : "s")} selected"; }
    }
    private async void SavePersonClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null) return;
        if (string.IsNullOrWhiteSpace(PersonName.Text)) { Notice("Enter a name for this person."); PersonName.Focus(); return; }
        if (_editingPerson is null && _references.Length == 0) { Notice("Choose at least one reference photo for a new person."); return; }
        var button = (Button)sender; button.IsEnabled = false;
        try
        {
            var saved = await _backend.SavePersonAsync(new() { PersonId = _editingPerson?.Id, Name = PersonName.Text.Trim(), Aliases = PersonAliases.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ReferencePaths = _references });
            await LoadPeopleAsync(); PeopleList.SelectedItem = _people.FirstOrDefault(p => p.Id == saved.Id); Notice($"Saved {saved.Name}. Future preparation uses these references.");
        }
        catch (Exception ex) { Notice($"Could not save this person: {ex.Message}"); }
        finally { button.IsEnabled = true; }
    }
    private async void SearchPersonClick(object sender, RoutedEventArgs e)
    {
        if (_editingPerson is null) return;
        Navigate("search"); _initialized = false;
        PersonFilter.SelectedItem = PersonFilter.Items.OfType<PersonChip>().FirstOrDefault(p => p.PersonId == _editingPerson.Id); _initialized = true; await SearchAsync(true);
    }

    private async void ReviewFacesClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || _editingPerson is null) return;
        try
        {
            var observations = await _backend.GetPersonObservationsAsync(_editingPerson.Id);
            var person = _editingPerson;
            var ordered = observations.OrderBy(o => o.Confirmed || o.Rejected).ToArray();
            var panel = new DockPanel { Margin = new Thickness(22) };
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = $"Face matches for {person.Name}", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 10) });
            heading.Children.Add(new TextBlock { Text = "Choose a correction, or select faces to move to a new person.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
            var count = new TextBlock { Margin = new(0, 0, 0, 15) }; heading.Children.Add(count); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
            var footer = new StackPanel { Margin = new(0, 15, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
            var movePanel = new StackPanel();
            var selectionHeading = new DockPanel { Margin = new(0, 0, 0, 8) };
            var clearSelection = new Button { Content = "Clear selection", Padding = new(10, 4, 10, 4), MinHeight = 28 }; DockPanel.SetDock(clearSelection, Dock.Right); selectionHeading.Children.Add(clearSelection);
            var selectionCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }; selectionHeading.Children.Add(selectionCount); movePanel.Children.Add(selectionHeading);
            var moveRow = new Grid(); moveRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); moveRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); moveRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var newName = new TextBox { MaxLength = 160, MinWidth = 120, Margin = new(8, 0, 8, 0) }; AutomationProperties.SetName(newName, "New person's name for selected faces"); Grid.SetColumn(newName, 1); moveRow.Children.Add(newName);
            moveRow.Children.Add(new Label { Content = "New person", Target = newName, VerticalAlignment = VerticalAlignment.Center, Padding = new(0) });
            var move = new Button { Content = "Move selected", IsEnabled = false }; AutomationProperties.SetName(move, "Move selected face observations to a new named person"); Grid.SetColumn(move, 2); moveRow.Children.Add(move); movePanel.Children.Add(moveRow);
            var moveArea = new Border { Child = movePanel, Padding = new(12), Margin = new(0, 0, 0, 12), CornerRadius = new(6), Visibility = Visibility.Collapsed }; moveArea.SetResourceReference(Border.BackgroundProperty, "TintBrush"); footer.Children.Add(moveArea);
            var actions = new DockPanel(); footer.Children.Add(actions);
            var save = new Button { Content = "Save corrections", HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(save, Dock.Right); actions.Children.Add(save);
            var previous = new Button { Content = "Previous", Margin = new(0, 0, 8, 0) }; var next = new Button { Content = "Next" };
            var navigation = new StackPanel { Orientation = Orientation.Horizontal }; navigation.Children.Add(previous); navigation.Children.Add(next); actions.Children.Add(navigation);
            var rows = new StackPanel(); var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; panel.Children.Add(scroll);
            var decisions = new Dictionary<long, int>();
            var selected = new HashSet<long>();
            var page = 0; const int pageSize = 40;
            CancellationTokenSource? pageImages = null;
            AutomationProperties.SetLiveSetting(selectionCount, AutomationLiveSetting.Polite);
            void UpdateSelection()
            {
                moveArea.Visibility = selected.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                selectionCount.Text = $"{selected.Count:N0} selected across all pages";
                move.IsEnabled = selected.Count > 0 && !string.IsNullOrWhiteSpace(newName.Text);
                save.IsEnabled = selected.Count == 0 && decisions.Any(decision => decision.Value is 1 or 2);
                save.ToolTip = selected.Count == 0 ? null : "Move the selected faces or clear the selection before saving corrections.";
            }
            newName.TextChanged += (_, _) => UpdateSelection();
            void ShowPage()
            {
                pageImages?.Cancel(); pageImages?.Dispose(); pageImages = new(); rows.Children.Clear();
                page = Math.Min(page, Math.Max(0, (ordered.Length - 1) / pageSize));
                var first = page * pageSize; var last = Math.Min(ordered.Length, first + pageSize);
                count.Text = ordered.Length == 0 ? "No matches yet. Prepare the archive after adding references." : $"{first + 1:N0}–{last:N0} of {ordered.Length:N0} available matches · uncertain matches first";
                previous.IsEnabled = page > 0; next.IsEnabled = last < ordered.Length;
                foreach (var observation in ordered.Skip(first).Take(pageSize))
                {
                    var line = new Grid { Margin = new Thickness(0, 0, 0, 10) }; line.ColumnDefinitions.Add(new() { Width = new(32) }); line.ColumnDefinitions.Add(new() { Width = new(64) }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = new(150) });
                    var select = new CheckBox { IsChecked = selected.Contains(observation.Id), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Select this face to move to a new person" }; AutomationProperties.SetName(select, $"Select face in {observation.RelativePath}" + (observation.StartMs is long timestamp ? $" at {FormatTime(timestamp)}" : "") + " for a new person"); line.Children.Add(select);
                    var portrait = new Image { Width = 54, Height = 54, Stretch = Stretch.UniformToFill }; Grid.SetColumn(portrait, 1); line.Children.Add(portrait);
                    _ = FillPortraitAsync(portrait, observation.Thumbnail, pageImages.Token);
                    var state = observation.Confirmed ? "Confirmed" : observation.Rejected ? "Rejected" : "Possible match";
                    var label = new TextBlock { Text = observation.RelativePath + (observation.StartMs is long t ? $"\n{FormatTime(t)}" : "") + $"\n{state}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new(9, 0, 9, 0), FontSize = 12 }; Grid.SetColumn(label, 2); line.Children.Add(label);
                    var choice = new ComboBox { ItemsSource = new[] { "Leave unchanged", "Confirm match", "Wrong person" }, SelectedIndex = decisions.GetValueOrDefault(observation.Id), VerticalAlignment = VerticalAlignment.Center, IsEnabled = !selected.Contains(observation.Id) }; AutomationProperties.SetName(choice, $"Correction for face in {observation.RelativePath}" + (observation.StartMs is long choiceTime ? $" at {FormatTime(choiceTime)}" : ""));
                    choice.SelectionChanged += (_, _) => { decisions[observation.Id] = choice.SelectedIndex; UpdateSelection(); };
                    select.Checked += (_, _) => { selected.Add(observation.Id); choice.IsEnabled = false; UpdateSelection(); };
                    select.Unchecked += (_, _) => { selected.Remove(observation.Id); choice.IsEnabled = true; UpdateSelection(); };
                    Grid.SetColumn(choice, 3); line.Children.Add(choice); rows.Children.Add(line);
                }
                UpdateSelection();
                scroll.ScrollToTop();
            }
            clearSelection.Click += (_, _) => { selected.Clear(); ShowPage(); };
            previous.Click += (_, _) => { page--; ShowPage(); }; next.Click += (_, _) => { page++; ShowPage(); };
            var dialog = CreateDialog("Review face matches", panel, 780, 650);
            dialog.MinWidth = 640; dialog.MinHeight = 420; dialog.MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 24);
            dialog.Closed += (_, _) => { pageImages?.Cancel(); pageImages?.Dispose(); };
            move.Click += async (_, _) =>
            {
                var name = newName.Text.Trim();
                if (name.Length is 0 or > 160 || name.Any(char.IsControl)) { MessageBox.Show(dialog, "Enter a name between 1 and 160 characters without control characters.", "Move selected faces"); newName.Focus(); return; }
                var ids = selected.ToArray(); if (ids.Length == 0) return;
                panel.IsEnabled = false;
                try
                {
                    var saved = await _backend.SavePersonAsync(new() { PersonId = null, Name = name, ConfirmObservationIds = ids });
                    ordered = ordered.Where(observation => !selected.Contains(observation.Id)).ToArray();
                    foreach (var id in ids) decisions.Remove(id);
                    selected.Clear(); newName.Clear(); ShowPage();
                    await LoadPeopleAsync();
                    PeopleList.SelectedItem = _people.FirstOrDefault(item => item.Id == person.Id);
                    Notice($"Moved {ids.Length:N0} face{(ids.Length == 1 ? "" : "s")} to {saved.Name}.");
                }
                catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Move selected faces"); }
                finally { panel.IsEnabled = true; }
            };
            save.Click += async (_, _) =>
            {
                if (selected.Count > 0 || !decisions.Any(decision => decision.Value is 1 or 2)) return;
                panel.IsEnabled = false;
                try { await _backend.SavePersonAsync(new() { PersonId = person.Id, Name = person.Name, Aliases = person.Aliases, ConfirmObservationIds = decisions.Where(d => d.Value == 1).Select(d => d.Key).ToArray(), RejectObservationIds = decisions.Where(d => d.Value == 2).Select(d => d.Key).ToArray() }); dialog.Close(); Notice("Face corrections saved."); await LoadPeopleAsync(); }
                catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Save corrections"); panel.IsEnabled = true; }
            };
            ShowPage();
            dialog.ShowDialog();
        }
        catch (Exception ex) { Notice($"Could not load face matches: {ex.Message}"); }
    }
    private async Task FillPortraitAsync(Image image, BlobReference? reference, CancellationToken token = default) { try { image.Source = await _thumbnails.ReadAsync(reference, token); } catch (Exception) { /* Missing portraits do not block corrections. */ } }
    private async void PersonPortraitLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: PersonRecord person } image) await FillPortraitAsync(image, person.Portrait);
    }
    private async void MomentThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: SearchMoment moment } image) { image.Visibility = moment.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible; image.Height = ActualHeight < 650 ? 30 : ActualHeight < 820 ? 36 : 70; await FillPortraitAsync(image, moment.Thumbnail); }
    }
    private void MergePersonClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || _editingPerson is null) return;
        var source = _editingPerson;
        var choices = _people.Where(p => p.Id != source.Id).ToArray();
        if (choices.Length == 0) { Notice("Add another person before merging names."); return; }
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = $"Merge {source.Name} into", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new(0, 20, 0, 15) }; panel.Children.Add(combo);
        panel.Children.Add(new TextBlock { Text = "Their reference photos and face matches will be combined.", TextWrapping = TextWrapping.Wrap });
        var button = new Button { Content = "Merge people", Margin = new(0, 20, 0, 0) }; panel.Children.Add(button);
        var dialog = CreateDialog("Merge people", panel, 420, 280);
        button.Click += async (_, _) => { if (combo.SelectedItem is not PersonRecord target) return; button.IsEnabled = false; try { await _backend.SavePersonAsync(new() { PersonId = source.Id, Name = source.Name, MergeIntoPersonId = target.Id }); dialog.Close(); await LoadPeopleAsync(); PeopleList.SelectedItem = _people.FirstOrDefault(person => person.Id == target.Id); Notice($"Merged {source.Name} into {target.Name}."); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Merge people"); button.IsEnabled = true; } };
        dialog.ShowDialog();
    }
    private Window CreateDialog(string title, object content, double width, double height) => new() { Owner = this, Title = title, Content = content, Width = width, Height = height, MinWidth = 350, MinHeight = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("CanvasBrush"), Foreground = (Brush)FindResource("InkBrush"), ShowInTaskbar = false };

    private async void FolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderEntry folder) return;
        Navigate("search"); _initialized = false; FolderFilter.SelectedItem = FolderFilter.Items.OfType<FolderChoice>().FirstOrDefault(f => f.Path == folder.RelativePath); _initialized = true; await SearchAsync(true);
    }
    private void AddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder on this archive drive", InitialDirectory = _root, Multiselect = true, AddToRecent = false };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FolderNames)
        {
            var relative = Path.GetRelativePath(_root, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) { Notice("Choose folders on this archive drive, so files travel with the index."); continue; }
            if (!_preparationFolders.Contains(relative)) _preparationFolders.Add(relative);
        }
    }
    private void RemoveFolderClick(object sender, RoutedEventArgs e) { foreach (var path in PreparationFolders.SelectedItems.Cast<string>().ToArray()) _preparationFolders.Remove(path); }
    private async void StartPreparationClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || _preparationTask is { IsCompleted: false }) return;
        if (_preparationFolders.Count == 0) { Notice("Add at least one folder to prepare."); return; }
        _prepareCancellation?.Dispose(); _prepareCancellation = new();
        StartPreparationButton.IsEnabled = false; PausePreparationButton.IsEnabled = true; ProgressCard.Visibility = Visibility.Visible;
        var progress = new Progress<PreparationProgress>(UpdateProgress);
        var request = new PreparationRequest { Folders = _preparationFolders.ToArray(), VerifyContents = VerifyContents.IsChecked == true, OcrLanguages = OcrLanguages.SelectedIndex switch { 1 => ["eng"], 2 => ["ara"], _ => ["eng", "ara"] } };
        _preparationTask = PrepareCoreAsync(request, progress, _prepareCancellation.Token);
        await _preparationTask;
    }
    private async Task PrepareCoreAsync(PreparationRequest request, IProgress<PreparationProgress> progress, CancellationToken token)
    {
        try { _status = await _backend!.PrepareAsync(request, progress, token); Notice("Preparation complete. Your updated archive is ready to search."); await RefreshStatusAsync(); await LoadPeopleAsync(); }
        catch (OperationCanceledException) { ProgressStage.Text = "Preparation paused"; ProgressMessage.Text = "Completed files are saved. Choose Prepare drive to resume."; }
        catch (Exception ex) { ProgressStage.Text = "Preparation stopped"; Notice($"Preparation could not continue: {ex.Message}"); }
        finally { StartPreparationButton.IsEnabled = true; StartPreparationButton.Content = "Prepare / resume"; PausePreparationButton.IsEnabled = false; }
    }
    private void UpdateProgress(PreparationProgress progress)
    {
        ProgressCard.Visibility = Visibility.Visible;
        ProgressStage.Text = progress.Stage switch { PreparationStage.Discovering => "Discovering files", PreparationStage.Extracting => "Reading your files", PreparationStage.Embedding => "Making content searchable", PreparationStage.RecognizingPeople => "Finding familiar faces", PreparationStage.Transcribing => "Listening for speech", PreparationStage.Publishing => "Saving this update", PreparationStage.Completed => "Ready to travel", PreparationStage.Paused => "Preparation paused", _ => "Preparing your archive" };
        PreparationProgressBar.IsIndeterminate = progress.Fraction is null; PreparationProgressBar.Value = Math.Clamp(progress.Fraction ?? 0, 0, 1);
        ProgressSummary.Text = $"{progress.Completed:N0} processed · {progress.Unchanged:N0} unchanged · {progress.Failed:N0} needing attention";
        ProgressPath.Text = progress.CurrentPath; ProgressMessage.Text = progress.Message;
        FooterStatus.Text = $"Preparing · {progress.Discovered:N0} files found · {progress.Segments:N0} searchable moments";
    }
    private void PausePreparationClick(object sender, RoutedEventArgs e) { _prepareCancellation?.Cancel(); PausePreparationButton.IsEnabled = false; ProgressMessage.Text = "Pausing after current work is safely checkpointed…"; }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { if (ActualHeight < 650) ClosePreview(); Navigate("search"); QueryBox.Focus(); QueryBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ClosePreview(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _selectedResult is not null) { OpenOriginal(); e.Handled = true; }
        else if (Keyboard.FocusedElement is Button { DataContext: ResultTile current } && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            var currentIndex = CurrentTiles.IndexOf(current);
            var target = e.Key switch { Key.Left => currentIndex - 1, Key.Right => currentIndex + 1, Key.Up => currentIndex - _columns, Key.Down => currentIndex + _columns, Key.Home => 0, Key.End => CurrentTiles.Count - 1, _ => currentIndex };
            if (target >= 0 && target < CurrentTiles.Count)
            {
                var targetTile = CurrentTiles[target]; var row = Rows[target / Math.Max(1, _columns)];
                Gallery.ScrollIntoView(row);
                Dispatcher.BeginInvoke(() => VisualChildren<Button>(Gallery).FirstOrDefault(button => ReferenceEquals(button.DataContext, targetTile))?.Focus(), DispatcherPriority.Loaded);
            }
            e.Handled = true;
        }
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject element) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is T item) yield return item;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private void ThemeClick(object sender, RoutedEventArgs e)
    {
        SetDarkTheme(!_dark);
    }
    public void SetDarkTheme(bool dark)
    {
        _dark = dark;
        var values = _dark ? new[] { "#18212D", "#212D3C", "#EDF2F8", "#B0BDD0", "#39495D", "#7099FF", "#293F68", "#79CCB6" } : new[] { "#F3F6F9", "#FFFFFF", "#223248", "#52647C", "#D8E1EB", "#2856CF", "#E9EFFD", "#187568" };
        var keys = new[] { "CanvasBrush", "SurfaceBrush", "InkBrush", "MutedBrush", "BorderBrush", "AccentBrush", "TintBrush", "ReadyBrush" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
        Application.Current.Resources["AccentButtonBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_dark ? "#315DD3" : "#2856CF"));
        if (SystemParameters.HighContrast)
        {
            Application.Current.Resources["CanvasBrush"] = SystemColors.WindowBrush;
            Application.Current.Resources["SurfaceBrush"] = SystemColors.WindowBrush;
            Application.Current.Resources["InkBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["BorderBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["AccentBrush"] = SystemColors.HotTrackBrush;
            Application.Current.Resources["AccentButtonBrush"] = SystemColors.HighlightBrush;
            Application.Current.Resources["TintBrush"] = SystemColors.ControlBrush;
            Application.Current.Resources["ReadyBrush"] = SystemColors.WindowTextBrush;
        }
    }
    private void Notice(string message, bool persistent = true)
    {
        if (!persistent && _noticePersistent) return;
        _noticePersistent = persistent; NoticeText.Text = message; NoticeBar.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void HideNotice(bool force = false) { if (_noticePersistent && !force) return; _noticePersistent = false; NoticeBar.Visibility = Visibility.Collapsed; }
    private void DismissNoticeClick(object sender, RoutedEventArgs e) => HideNotice(true);
    private string SafePath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("File path is outside this archive.");
        return full;
    }
    private static string FormatTime(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss");
    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):N1} GB" : $"{value / (1024d * 1024):N0} MB";
    private void EjectClick(object sender, RoutedEventArgs e) { _ejectRequested = true; Close(); }
    private void UpdatesClick(object sender, RoutedEventArgs e)
    {
        UpdatesButton.ContextMenu.PlacementTarget = UpdatesButton;
        UpdatesButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        UpdatesButton.ContextMenu.IsOpen = true;
    }
    private async void CheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_updating || _closing) return;
        _updating = true; _updateCancellation = new();
        try
        {
            Notice("Checking for application updates…");
            var result = await new PortableUpdateManager(_root).CheckAsync(_updateCancellation.Token);
            Notice(result.Message);
            if (result.VersionAvailable && result.DownloadUrl is not null)
            {
                var panel = new StackPanel { Margin = new Thickness(24) };
                panel.Children.Add(new TextBlock { Text = result.Version is null ? "An update is available" : $"FindIt {result.Version} is available", FontSize = 21, FontWeight = FontWeights.SemiBold });
                panel.Children.Add(new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 20) });
                var open = new Button { Content = "Open download page", HorizontalAlignment = HorizontalAlignment.Left };
                panel.Children.Add(open);
                var dialog = CreateDialog("Application update", panel, 470, 270);
                open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(result.DownloadUrl) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Open download page"); } };
                dialog.ShowDialog();
            }
        }
        catch (OperationCanceledException) { if (!_closing) Notice("Update check cancelled."); }
        catch (Exception ex) { if (!_closing) Notice($"Could not check for updates: {ex.Message}"); }
        finally { _updating = false; _updateCancellation?.Dispose(); _updateCancellation = null; }
    }
    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updating || _closing) return;
        var picker = new OpenFileDialog { Title = "Choose a signed FindIt update package", Filter = "FindIt update package|*.zip", CheckFileExists = true, AddToRecent = false, RestoreDirectory = true };
        if (picker.ShowDialog(this) != true) return;
        _updating = true; _updateCancellation = new();
        try
        {
            Notice("Validating the update's publisher signature and files…");
            var result = await new PortableUpdateManager(_root).InstallOfflineAsync(picker.FileName, _updateCancellation.Token);
            Notice(result.Message);
        }
        catch (OperationCanceledException) { if (!_closing) Notice("Update installation cancelled."); }
        catch (Exception ex) { if (!_closing) Notice($"Could not install this update: {ex.Message}"); }
        finally { _updating = false; _updateCancellation?.Dispose(); _updateCancellation = null; }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _statusTimer.Stop(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel(); _prepareCancellation?.Cancel(); _updateCancellation?.Cancel();
        FooterStatus.Text = "Closing archive and releasing this drive…";
        try
        {
            if (_preparationTask is not null) await _preparationTask;
            await DisposePlaybackAsync();
            if (_backend is not null) await _backend.DisposeAsync();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        if (_ejectRequested)
        {
            var result = await DriveEjector.RequestAsync(_root);
            MessageBox.Show(this, result.Message, "Eject drive", MessageBoxButton.OK, result.Requested ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        _closed = true; Close();
    }
}
