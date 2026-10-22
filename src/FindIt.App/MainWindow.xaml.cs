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
    private int? _searchBudget;
    private bool _boundedSearch;
    private SearchFilters _submittedFilters = new();
    private bool _initialized, _searching, _statusPolling, _dark, _closing, _closed, _ejectRequested;
    private bool _preparationUiBusy;
    private IReadOnlyList<FolderEntry> _displayFolders = [];
    private bool _noticePersistent;
    private bool _showingPreparedFrame;
    private string? _searchError, _dismissedStatusNotice;
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
        FiltersPopup.CustomPopupPlacementCallback = (popup, target, offset) => [new(new Point(target.Width - popup.Width, target.Height), System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical), new(new Point(target.Width - popup.Width, -popup.Height), System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical)];
        _uiReady = true;
        InitializeAppearance();
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
        UpdatePreparationControls();
        KindFilter.SelectedIndex = DateFilter.SelectedIndex = 0;
        FolderFilter.ItemsSource = new[] { new FolderChoice("All folders", null) }; FolderFilter.SelectedIndex = 0;
        PersonFilter.ItemsSource = new[] { new PersonChip(Guid.Empty, "Anyone") }; PersonFilter.SelectedIndex = 0;
        Navigate("search");
        if (_backend is null) { FooterStatus.Text = "Opening archive…"; EmptyTitle.Text = "Opening your archive"; EmptyDescription.Text = "Reading the library on this drive. Your files will appear here shortly."; EmptyAction.IsEnabled = false; StartPreparationButton.IsEnabled = false; return; }
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
    public void ShowRenderPage(string page) { Navigate(page); if (_backend is null) { UpdateEmptyState(); EmptyAction.IsEnabled = true; } FooterStatus.Text = "UI preview · no archive opened"; }
    public async Task RenderArchiveAsync(string? query, bool previewFirst, string? kind = null, bool selectPerson = false, bool exact = false)
    {
        _initialized = false;
        ExactFilter.IsChecked = exact;
        KindFilter.SelectedIndex = kind?.ToLowerInvariant() switch { "photo" or "image" => 1, "video" => 2, "document" => 3, "audio" => 4, _ => 0 };
        _initialized = true;
        QueryBox.Text = query ?? "";
        await SearchAsync(true, submitQuery: true);
        if (previewFirst && CurrentTiles.FirstOrDefault() is ResultTile first) await SelectResultAsync(first.Result);
        if (selectPerson && _people.FirstOrDefault() is PersonRecord person) PeopleList.SelectedItem = person;
        FooterStatus.Text = $"UI capture · {_status.FileCount:N0} real archive files";
    }
    public async Task PrepareCaptureAsync()
    {
        if (_preview?.Kind != FileKind.Video) return;
        // RenderTargetBitmap cannot capture a foreign HWND. Show the actual indexed frame in captures.
        var evidence = _preview.PersonEvidence.FirstOrDefault();
        var frame = evidence?.Thumbnail ?? evidence?.Portrait ?? _preview.Moments.FirstOrDefault(m => m.StartMs == _preview.StartMs)?.Thumbnail ?? _selectedResult?.Thumbnail;
        PreviewImage.Source = await _thumbnails.ReadAsync(frame, CancellationToken.None);
        VideoPlayer.Visibility = Visibility.Collapsed;
        FooterStatus.Text += " · Video surface shown as its indexed frame";
    }
    private void Navigate(string page)
    {
        WorkspaceTitle.Text = page switch { "people" => "People", "folders" => "Folders", "prepare" => "Prepare drive", _ => "Library" };
        SearchPage.Visibility = page == "search" ? Visibility.Visible : Visibility.Collapsed;
        PeoplePage.Visibility = page == "people" ? Visibility.Visible : Visibility.Collapsed;
        FoldersPage.Visibility = page == "folders" ? Visibility.Visible : Visibility.Collapsed;
        PreparePage.Visibility = page == "prepare" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, name) in new[] { (SearchNav, "search"), (PeopleNav, "people"), (FoldersNav, "folders"), (PrepareNav, "prepare") })
        {
            button.SetResourceReference(BackgroundProperty, page == name ? "TintBrush" : "SidebarBrush");
            button.SetResourceReference(ForegroundProperty, page == name ? "AccentBrush" : "InkBrush");
            button.FontWeight = page == name ? FontWeights.SemiBold : FontWeights.Normal;
        }
        FiltersPopup.IsOpen = false; if (page != "search") ClosePreview(); ApplyResponsiveLayout();
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
            var wasConnected = _status.Connected;
            var wasSemantic = _status.SemanticAvailable;
            _status = Directory.Exists(_root) ? await _backend.GetStatusAsync() : _status with { Connected = false, StatusDetail = "Drive disconnected." };
            if (previousLibrary != Guid.Empty && _status.LibraryId != Guid.Empty && previousLibrary != _status.LibraryId)
            {
                _searchCancellation?.Cancel(); ClosePreview(); CurrentTiles.Clear(); Rows.Clear(); _searchGeneration = null;
                _pages.Clear(); _pageLru.Clear(); _pageCursors.Clear(); _pageCursors.Add(null); _page = 0;
                _boundedSearch = false; SearchMoreButton.Visibility = Visibility.Collapsed;
                var initialized = _initialized; _initialized = false; PersonFilter.SelectedIndex = 0; _initialized = initialized; CaptureFilterState(); UpdateFilterUi();
                await LoadPeopleAsync();
                Notice("A different archive is connected. Run your search to see this drive's files. Reopen a person before editing their details.");
            }
            if (wasConnected != _status.Connected || wasSemantic != _status.SemanticAvailable) { _dismissedStatusNotice = null; if (_status.Connected) HideNotice(true); }
            UpdatePreparationControls();
            DriveName.Text = _status.DisplayName;
            ConnectionLabel.Text = !_status.Connected ? "Drive disconnected" : _status.IsPreparing ? "Preparing" : _status.FileCount > 0 ? "Ready to search" : "Not prepared yet";
            ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _status.Connected ? "ReadyBrush" : "MutedBrush");
            FooterStatus.Text = !_status.Connected ? "Reconnect this drive to continue." : $"{_status.FileCount:N0} files · {_status.SegmentCount:N0} searchable moments · {FormatBytes(_status.TotalBytes)}";
            if (!_displayFolders.SequenceEqual(_status.Folders))
            {
                var selectedPath = (FolderList.SelectedItem as FolderEntry)?.RelativePath;
                _displayFolders = _status.Folders;
                SidebarFolderList.ItemsSource = _displayFolders; FolderList.ItemsSource = _displayFolders;
                FolderList.SelectedItem = _displayFolders.FirstOrDefault(folder => folder.RelativePath == selectedPath);
            }
            FolderEmpty.Visibility = _status.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CapabilitiesList.ItemsSource = _status.Capabilities;
            IssuesList.ItemsSource = _status.Issues;
            IssuesHeading.Text = _status.Issues.Count == 0 ? "No files need attention" : $"{_status.Issues.Count:N0} files needing attention";
            var selectedFolder = (FolderFilter.SelectedItem as FolderChoice)?.Path ?? _filters.Folder;
            _initialized = false;
            var choices = new[] { new FolderChoice("All folders", null) }.Concat(_status.Folders.Select(f => new FolderChoice(f.RelativePath, f.RelativePath))).ToArray();
            if (selectedFolder is not null && !choices.Any(f => f.Path == selectedFolder)) choices = choices.Append(new FolderChoice(selectedFolder, selectedFolder)).ToArray();
            FolderFilter.ItemsSource = choices; FolderFilter.SelectedItem = choices.FirstOrDefault(f => f.Path == selectedFolder) ?? choices[0];
            _initialized = true;
            if (!_status.Connected)
            {
                StopPlayback(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel();
                Notice("Drive disconnected. Reconnect the same drive to continue. Your query is preserved.");
            }
            else if (_status.IsPreparing) Notice("Preparation is running. Search currently prepared files while the rest are processed.", false);
            else if (_status.FileCount > 0 && !_status.SemanticAvailable) Notice("Visual search is unavailable. You can still search filenames and exact words. See Prepare drive for details.", false);
            else if (!_searching) HideNotice();
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

    private SearchFilters ReadFilters() => _filters.ToSearchFilters(DateTimeOffset.Now, TimeZoneInfo.Local);

    private async Task SearchAsync(bool reset, int? targetPage = null, bool submitQuery = false, bool deepen = false)
    {
        if (_backend is null || _closing) return;
        CaptureFilterState();
        if (_filters.ValidateDateRange() is { } validation) { Notice(validation); return; }
        UpdateFilterUi();
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = new();
        if (reset)
        {
            if (!deepen) _searchBudget = null;
            _boundedSearch = false;
            _page = 0; _pageCursors.Clear(); _pageCursors.Add(null); _pages.Clear(); _pageLru.Clear(); _searchGeneration = null; ClosePreview();
            if (submitQuery) _submittedQuery = QueryBox.Text.Trim();
            _submittedFilters = ReadFilters();
        }
        var requestedPage = targetPage ?? _page;
        _searchError = null;
        _searching = true; SearchProgress.Visibility = Visibility.Visible; ResultCount.Text = "Searching…";
        PreviousPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        SearchMoreButton.IsEnabled = false;
        if (CurrentTiles.Count == 0) UpdateEmptyState();
        try
        {
            // Even a previously viewed page validates its worker session. Cached page
            // metadata must not bypass expiry, generation changes or people corrections.
            var response = await _backend.SearchAsync(new() { Query = _submittedQuery, Filters = _submittedFilters, Limit = 256,
                Cursor = _pageCursors.ElementAtOrDefault(requestedPage), GenerationId = _searchGeneration, RetrievalBudget = _searchBudget }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (response.RestartRequired)
            {
                _page = 0; _pageCursors.Clear(); _pageCursors.Add(null); _pages.Clear(); _pageLru.Clear(); _searchGeneration = null;
                ClosePreview(); CurrentTiles.Clear(); Rows.Clear();
                _searchError = string.IsNullOrWhiteSpace(response.Notice) ? "Run the search again." : response.Notice;
                ResultCount.Text = "Search needs restarting"; Notice(_searchError);
                SearchMoreButton.Visibility = Visibility.Collapsed;
                return;
            }
            _searchGeneration = response.GenerationId;
            _boundedSearch = response.IsBoundedWindow;
            if (response.PageCursor is not null) _pageCursors[requestedPage] = response.PageCursor;
            if (!_pages.ContainsKey(requestedPage)) _pageLru.Enqueue(requestedPage);
            _pages[requestedPage] = response;
            while (_pages.Count > 8 && _pageLru.TryDequeue(out var oldest)) _pages.Remove(oldest);
            if (response.NextCursor is not null && _pageCursors.Count == requestedPage + 1) _pageCursors.Add(response.NextCursor);
            if (_page != requestedPage) ClosePreview();
            _page = requestedPage;
            CurrentTiles.Clear();
            foreach (var result in response.Results) CurrentTiles.Add(new ResultTile { Result = result });
            ArrangeRows(true); GalleryScrollToTop();
            EmptyState.Visibility = CurrentTiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var first = _page * 256 + 1; var last = _page * 256 + CurrentTiles.Count;
            ResultCount.Text = CurrentTiles.Count == 0 ? "No results" : $"{first:N0}–{last:N0}{(response.TotalCount is long total ? $" of {total:N0}" : "")}{(response.NextCursor is not null && response.TotalCount is null ? " · More results available" : "")}";
            if (response.IsBoundedWindow) ResultCount.Text += " · Ranked selection";
            SearchMoreButton.Visibility = response.CanSearchDeeper ? Visibility.Visible : Visibility.Collapsed;
            SearchMoreButton.IsEnabled = response.CanSearchDeeper;
            SearchMoreButton.Tag = response.RetrievalBudget;
            PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = response.NextCursor is not null;
            if (!string.IsNullOrWhiteSpace(response.Notice)) Notice(response.Notice); else if (!_status.IsPreparing) HideNotice(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                Notice($"Search could not finish: {ex.Message}");
                if (reset)
                {
                    _searchError = ex.Message; CurrentTiles.Clear(); Rows.Clear();
                    ResultCount.Text = "Search interrupted";
                }
                else
                {
                    PreviousPageButton.IsEnabled = _page > 0;
                    NextPageButton.IsEnabled = _pages.TryGetValue(_page, out var visiblePage) && visiblePage.NextCursor is not null;
                    SearchMoreButton.IsEnabled = visiblePage?.CanSearchDeeper == true;
                    ResultCount.Text = $"Page {_page + 1:N0} · Could not load that page. Try again.";
                }
            }
        }
        finally
        {
            if (ReferenceEquals(cancellation, _searchCancellation))
            {
                _searching = false; SearchProgress.Visibility = Visibility.Collapsed;
                if (CurrentTiles.Count == 0) UpdateEmptyState();
            }
        }
    }

    private async void TileLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResultTile tile || tile.Thumbnail is not null || _thumbnailRequests.ContainsKey(tile)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_searchCancellation?.Token ?? CancellationToken.None);
        _thumbnailRequests[tile] = cancellation;
        try
        {
            var thumbnail = await _thumbnails.ReadAsync(tile.Result.Thumbnail, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                var previousRatio = tile.AspectRatio; tile.Thumbnail = thumbnail;
                if (Math.Abs(previousRatio - tile.AspectRatio) > .01) QueueThumbnailReflow();
            }
        }
        catch (OperationCanceledException) { }
        finally { if (_thumbnailRequests.TryGetValue(tile, out var active) && ReferenceEquals(active, cancellation)) _thumbnailRequests.Remove(tile); cancellation.Dispose(); }
    }
    private void TileUnloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResultTile tile) return;
        if (_thumbnailRequests.Remove(tile, out var request)) request.Cancel();
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

    private async void ResultClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ResultTile tile) await SelectResultAsync(tile.Result); }
    private async void ResultSelectionChanged(object sender, SelectionChangedEventArgs e) { if (MetadataList.SelectedItem is ResultTile tile && tile.Result.FileId != _selectedResult?.FileId) await SelectResultAsync(tile.Result); }
    private void ListDoubleClick(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(MetadataList, source) is ListViewItem { DataContext: ResultTile tile } && tile.Result.FileId == _selectedResult?.FileId) OpenOriginal(); }

    private void UpdateEmptyState()
    {
        var prepared = _status.FileCount > 0;
        EmptyTitle.Text = !_status.Connected ? "Your drive is disconnected" : _searching ? "Searching your archive" : _searchError is not null ? "Run your search again" : prepared ? _boundedSearch ? "No matches in this selection" : "No matches yet" : "Your archive, ready to discover";
        EmptyDescription.Text = !_status.Connected ? "Reconnect this drive to pick up where you left off." : _searching ? "Finding files that match your search." : _searchError is not null ? "Try again. Your search and filters are still here." : prepared ? "Try a broader description, remove a filter, or search for words you remember." : "Choose folders to make your photos, videos and documents searchable offline.";
        EmptyAction.Content = _searchError is not null ? "Try search again" : prepared ? "Clear search and filters" : "Prepare this drive";
        EmptyAction.Visibility = _searching ? Visibility.Collapsed : Visibility.Visible;
        EmptyAction.IsEnabled = _status.Connected && _backend is not null;
        EmptyState.Visibility = Visibility.Visible;
    }
    private async void EmptyActionClick(object sender, RoutedEventArgs e)
    {
        if (_searchError is not null) { await SearchAsync(true); return; }
        if (_status.FileCount == 0) { Navigate("prepare"); return; }
        QueryBox.Clear(); ResetFilterControls(); await SearchAsync(true, submitQuery: true);
    }

    private async Task SelectResultAsync(SearchResult result, long? segmentId = null)
    {
        if (_backend is null) return;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose();
        var cancellation = _previewCancellation = new();
        _selectedResult = result; _lastResultId = result.FileId; _preview = null; StopPlayback();
        foreach (var tile in CurrentTiles) tile.IsSelected = tile.Result.FileId == result.FileId;
        MomentsList.ItemsSource = null; MomentsHeading.Visibility = Visibility.Collapsed; PreviewText.Visibility = Visibility.Visible;
        PreviewName.Text = result.Name; PreviewPath.Text = result.RelativePath; PreviewText.Text = result.Snippet;
        PreviewMediaRow.Height = new GridLength(200);
        PreviewImage.Source = null; PreviewPlaceholder.Text = "Loading preview…"; PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPanel.Visibility = Visibility.Visible; ApplyResponsiveLayout(); UpdateLayout();
        PreviewDetails.Text = $"{result.Kind} · {FormatBytes(result.Bytes)} · {result.ModifiedUtc.ToLocalTime():dd MMM yyyy}";
        PossibleMatchBanner.Visibility = result.MatchCategory == MatchCategory.PossiblePersonAction ? Visibility.Visible : Visibility.Collapsed;
        PersonEvidenceDetails.Visibility = Visibility.Collapsed;
        PersonEvidenceFaces.Visibility = Visibility.Collapsed; PersonEvidenceFaces.ItemsSource = null;
        EvidencePortrait.Source = null; EvidencePortrait.Visibility = Visibility.Collapsed;
        try
        {
            var preview = await _backend.GetPreviewAsync(ResultPreview.Request(result, segmentId), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); _preview = preview;
            PreviewText.Text = string.IsNullOrWhiteSpace(preview.Text) ? result.Snippet : preview.Text;
            PreviewText.Visibility = string.IsNullOrWhiteSpace(PreviewText.Text) ? Visibility.Collapsed : Visibility.Visible;
            var moments = result.MatchedPersonIds.Count > 0 ? result.Moments : preview.Moments.Count > 0 ? preview.Moments : result.Moments;
            var showMoments = moments.Count > 1 || result.Kind is FileKind.Video or FileKind.Audio;
            MomentsList.ItemsSource = showMoments ? moments : [];
            MomentsHeading.Visibility = showMoments ? Visibility.Visible : Visibility.Collapsed;
            if (preview.PersonEvidence.Count > 0)
            {
                var time = preview.PersonEvidence[0].StartMs;
                PersonEvidenceDetails.Text = "Face evidence: " + string.Join(", ", preview.PersonEvidence.Select(e => e.Name).Distinct()) + (time is long at ? $" at {TimeSpan.FromMilliseconds(at):hh\\:mm\\:ss\\.fff}" : "") + ". A sampled face does not verify an action or the speaker.";
                PersonEvidenceDetails.Visibility = Visibility.Visible;
                var faceTiles = new List<EvidenceFaceTile>();
                foreach (var evidence in preview.PersonEvidence)
                {
                    var portrait = await _thumbnails.ReadAsync(evidence.Portrait, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var timestamp = evidence.StartMs is long sample ? $"{TimeSpan.FromMilliseconds(sample):hh\\:mm\\:ss\\.fff} · " : "";
                    faceTiles.Add(new(evidence.Name, timestamp + (evidence.Confirmed ? "Confirmed membership" : "Suggested membership"), portrait));
                }
                PersonEvidenceFaces.ItemsSource = faceTiles; PersonEvidenceFaces.Visibility = Visibility.Visible;
                if (faceTiles.FirstOrDefault() is { Portrait: not null } firstFace)
                {
                    EvidencePortrait.Source = firstFace.Portrait; EvidencePortrait.Visibility = Visibility.Visible;
                    EvidencePortrait.ToolTip = firstFace.AccessibleName; AutomationProperties.SetName(EvidencePortrait, firstFace.AccessibleName);
                }
            }
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
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) { PreviewPlaceholder.Text = "Preview unavailable. Open the original file to view it."; PreviewPlaceholder.Visibility = Visibility.Visible; Notice($"Could not load preview: {ex.Message}"); } }
        if (cancellation.IsCancellationRequested) return;
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
            var audioOnly = preview.Kind == FileKind.Audio;
            var evidence = preview.PersonEvidence.FirstOrDefault();
            var frame = evidence?.Thumbnail ?? evidence?.Portrait;
            var poster = audioOnly ? null : await _thumbnails.ReadAsync(frame, token);
            token.ThrowIfCancellationRequested();
            _showingPreparedFrame = poster is not null;
            PreviewImage.Source = poster;
            PreviewImage.ToolTip = _showingPreparedFrame
                ? evidence?.Thumbnail is not null
                    ? "Sampled face frame. Play or seek to view surrounding footage."
                    : "Sampled face crop from this older library. Play or seek to view surrounding footage."
                : null;
            if (_showingPreparedFrame && evidence?.Thumbnail is null)
                PersonEvidenceDetails.Text += " Showing saved face crop; context frame unavailable.";
            VideoPlayer.Width = VideoPlayer.Height = audioOnly ? 1 : double.NaN;
            VideoPlayer.HorizontalAlignment = audioOnly ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            VideoPlayer.VerticalAlignment = audioOnly ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            VideoPlayer.Visibility = _showingPreparedFrame ? Visibility.Hidden : Visibility.Visible; VideoControls.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = audioOnly ? "♪\nAudio preview" : "Loading preview…";
            PreviewPlaceholder.Visibility = audioOnly ? Visibility.Visible : Visibility.Collapsed;
            VideoPlayer.UpdateLayout();
            _player ??= new PreviewPlayerClient(_root, VideoPlayer.WindowHandle);
            PlayPauseButton.Content = "Play"; AutomationProperties.SetName(PlayPauseButton, "Play playback");
            PreviewTime.Text = FormatTime(preview.StartMs ?? 0);
            await _player.OpenAsync(preview.OriginalPath, preview.StartMs ?? 0, token); StartPlaybackPolling();
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
    private void StopPlayback() { _showingPreparedFrame = false; StopPlaybackPolling(); _ = StopPlaybackAsync(); VideoPlayer.Visibility = Visibility.Collapsed; VideoControls.Visibility = Visibility.Collapsed; }
    private void ShowDecodedVideo()
    {
        if (!_showingPreparedFrame || _preview?.Kind != FileKind.Video) return;
        _showingPreparedFrame = false; PreviewImage.Source = null; PreviewImage.ToolTip = null; VideoPlayer.Visibility = Visibility.Visible;
    }
    private async void PlayPauseClick(object sender, RoutedEventArgs e) { if (_player is null) return; try { await _player.PauseAsync(); ShowDecodedVideo(); } catch (Exception ex) { Notice($"Playback interrupted: {ex.Message}"); } }
    private async void MomentClick(object sender, RoutedEventArgs e) { if (_selectedResult is not null && (sender as FrameworkElement)?.Tag is SearchMoment moment) await SelectResultAsync(_selectedResult, moment.SegmentId); }
    private void ClosePreviewClick(object sender, RoutedEventArgs e) => ClosePreview(true);
    private void ClosePreview(bool restoreFocus = false) { _previewCancellation?.Cancel(); StopPlayback(); _preview = null; _selectedResult = null; MetadataList.SelectedItem = null; foreach (var tile in CurrentTiles) tile.IsSelected = false; ApplyResponsiveLayout(); ArrangeRows(true); if (restoreFocus) RestoreResultFocus(); }
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

    private async void FolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderEntry folder) return;
        Navigate("search"); _initialized = false; FolderFilter.SelectedItem = FolderFilter.Items.OfType<FolderChoice>().FirstOrDefault(f => f.Path == folder.RelativePath); _initialized = true; await SearchAsync(true);
    }
    private void AddFolderClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || !_status.Connected || _preparationUiBusy) return;
        var dialog = new OpenFolderDialog { Title = "Choose a folder on this archive drive", InitialDirectory = _root, Multiselect = true, AddToRecent = false };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FolderNames)
        {
            var relative = Path.GetRelativePath(_root, path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) { Notice("Choose folders on this archive drive, so files travel with the index."); continue; }
            if (!_preparationFolders.Contains(relative)) _preparationFolders.Add(relative);
        }
    }
    private void RemoveFolderClick(object sender, RoutedEventArgs e) { foreach (var path in PreparationFolders.SelectedItems.Cast<string>().ToArray()) _preparationFolders.Remove(path); }
    private async void StartPreparationClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || !_status.Connected || _preparationTask is { IsCompleted: false }) return;
        if (_preparationFolders.Count == 0) { Notice("Add at least one folder to prepare."); return; }
        _prepareCancellation?.Dispose(); _prepareCancellation = new();
        _preparationUiBusy = true; UpdatePreparationControls(); ProgressCard.Visibility = Visibility.Visible;
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
        finally { _preparationUiBusy = false; StartPreparationButton.Content = "Prepare / resume"; UpdatePreparationControls(); }
    }
    private void UpdatePreparationControls()
    {
        var available = _backend is not null && _status.Connected && !_closing;
        var busy = _preparationUiBusy || _status.IsPreparing;
        StartPreparationButton.IsEnabled = available && !busy;
        PausePreparationButton.IsEnabled = available && _preparationUiBusy && _prepareCancellation?.IsCancellationRequested != true;
        foreach (var control in new Control[] { AddPreparationFolderButton, RemovePreparationFolderButton, PreparationFolders, OcrLanguages, VerifyContents }) control.IsEnabled = available && !busy;
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
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Navigate("search"); QueryBox.Focus(); QueryBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            var openCombo = VisualChildren<ComboBox>(this).FirstOrDefault(combo => combo.IsDropDownOpen) ?? VisualChildren<ComboBox>(FiltersPopup.Child).FirstOrDefault(combo => combo.IsDropDownOpen);
            if (openCombo is not null) { openCombo.IsDropDownOpen = false; openCombo.Focus(); }
            else if (VisualChildren<DatePicker>(this).Concat(VisualChildren<DatePicker>(FiltersPopup.Child)).FirstOrDefault(picker => picker.IsDropDownOpen) is { } openDate) { openDate.IsDropDownOpen = false; openDate.Focus(); }
            else if (FiltersPopup.IsOpen) { FiltersPopup.IsOpen = false; FiltersButton.Focus(); }
            else if (PeoplePage.Visibility == Visibility.Visible) ClosePersonClick(this, e);
            else ClosePreview(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _selectedResult is not null) { OpenOriginal(); e.Handled = true; }
        else if (Keyboard.FocusedElement is Button { DataContext: ResultTile current } && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            var index = CurrentTiles.IndexOf(current);
            var target = e.Key switch { Key.Left => index - 1, Key.Right => index + 1, Key.Up => ContactSheetLayout.FindVerticalNeighbor(_sheetRows, index, -1), Key.Down => ContactSheetLayout.FindVerticalNeighbor(_sheetRows, index, 1), Key.Home => 0, Key.End => CurrentTiles.Count - 1, _ => index };
            if (target >= 0 && target < CurrentTiles.Count)
            {
                var tile = CurrentTiles[target];
                Gallery.ScrollIntoView(Rows[ContactSheetLayout.GetRowIndex(_sheetRows, target)]);
                Dispatcher.BeginInvoke(() => VisualChildren<Button>(Gallery).FirstOrDefault(button => ReferenceEquals(button.DataContext, tile))?.Focus(), DispatcherPriority.Loaded);
            }
            e.Handled = true;
        }
        else if (Keyboard.FocusedElement is Button { DataContext: PersonTile person } && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            var index = _personTiles.IndexOf(person);
            var columns = Math.Max(1, _peopleColumns);
            var target = e.Key switch { Key.Left => index - 1, Key.Right => index + 1, Key.Up => index - columns, Key.Down => index + columns, Key.Home => 0, Key.End => _personTiles.Count - 1, _ => index };
            if (target >= 0 && target < _personTiles.Count)
            {
                var tile = _personTiles[target]; PeopleGallery.ScrollIntoView(PeopleRows[target / columns]);
                Dispatcher.BeginInvoke(() => VisualChildren<Button>(PeopleGallery).FirstOrDefault(button => ReferenceEquals(button.DataContext, tile))?.Focus(), DispatcherPriority.Loaded);
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
    private void Notice(string message, bool persistent = true)
    {
        if (!persistent && (_noticePersistent || _dismissedStatusNotice == message)) return;
        _noticePersistent = persistent; NoticeText.Text = message; NoticeBar.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void HideNotice(bool force = false) { if (_noticePersistent && !force) return; _noticePersistent = false; NoticeBar.Visibility = Visibility.Collapsed; }
    private void DismissNoticeClick(object sender, RoutedEventArgs e) { if (!_noticePersistent) _dismissedStatusNotice = NoticeText.Text; HideNotice(true); }
    private string SafePath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("File path is outside this archive.");
        return full;
    }
    private static string FormatTime(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss");
    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):N1} GB" : value >= 1024L * 1024 ? $"{value / (1024d * 1024):N1} MB" : value >= 1024 ? $"{value / 1024d:N1} KB" : $"{value:N0} B";
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
                open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(result.DownloadUrl) { UseShellExecute = true }); } catch (Exception ex) { ThemedDialog.Show(dialog, ex.Message, "Open download page"); } };
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
        if (!TryLeavePersonEditor()) { _ejectRequested = false; return; }
        _closing = true; foreach (var request in _portraitRequests.Values.ToArray()) request.Cancel(); DisposeAppearance(); StopPlaybackPolling(); _statusTimer.Stop(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel(); _prepareCancellation?.Cancel(); _updateCancellation?.Cancel();
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
            ThemedDialog.Show(this, result.Message, "Eject drive", MessageBoxButton.OK, result.Requested ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        _closed = true; Close();
    }
}
