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
    private bool _preparationUiBusy, _preparationPaused;
    private IReadOnlyList<FolderEntry> _displayFolders = [];
    private bool _noticePersistent;
    private bool _showingPreparedFrame;
    private string? _searchError, _dismissedStatusNotice;
    private PreviewPlayerClient? _player;
    private readonly SemaphoreSlim _playbackGate = new(1);
    private readonly Dictionary<ResultTile, CancellationTokenSource> _thumbnailRequests = [];
    public ObservableCollection<ResultRow> Rows { get; } = [];
    public ObservableCollection<ResultTile> CurrentTiles { get; } = [];
    public ObservableCollection<ResultTile> RecentTiles { get; } = [];
    private string _view = "home";
    private readonly List<string> _recentQueries = [];
    private string _previewPlainText = "";
    private const double GalleryGap = 12, GalleryCaption = 44;

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
        _preparationFolders.CollectionChanged += (_, _) =>
        {
            PreparationFoldersEmpty.Visibility = _preparationFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RemovePreparationFolderButton.Visibility = _preparationFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        RemovePreparationFolderButton.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(NoticeText, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(ResultCount, AutomationLiveSetting.Polite);
        DriveLocation.Text = _root;
        ShortcutList.ItemsSource = new ShortcutEntry[] { new("Search", "Ctrl K"), new("Search for what you typed", "Enter"), new("Move between files", "Arrow keys"), new("Open the selected file", "Ctrl Enter"), new("Close the preview or a person", "Esc") };
        BuildStageStrip(); BuildSuggestions();
        // Nobody sees a minimized window; restoring it refreshes at once.
        _statusTimer.Tick += async (_, _) => { if (WindowState != WindowState.Minimized) await RefreshStatusAsync(); };
        _progressTimer.Tick += (_, _) => ShowPendingProgress();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        UpdatePreparationControls();
        KindFilter.SelectedIndex = DateFilter.SelectedIndex = 0;
        FolderFilter.ItemsSource = new[] { new FolderChoice("All folders", null) }; FolderFilter.SelectedIndex = 0;
        PersonFilter.ItemsSource = new[] { new PersonChip(Guid.Empty, "Anyone") }; PersonFilter.SelectedIndex = 0;
        Navigate("home");
        if (_backend is null) { FooterStatus.Text = "Opening archive…"; EmptyTitle.Text = "Opening your archive"; EmptyDescription.Text = "Reading the library on this drive. Your files will appear here shortly."; EmptyAction.IsEnabled = false; StartPreparationButton.IsEnabled = false; return; }
        StartPreparationButton.IsEnabled = true;
        await RefreshStatusAsync();
        RestoreDrivePreferences();
        await LoadPeopleAsync();
        _initialized = true;
        _statusTimer.Start();
        // The newest-first browse window feeds both Browse and the home page's recent files.
        if (_status.FileCount > 0) await SearchAsync(true);
        (HomePage.IsVisible ? HeroQueryBox : QueryBox).Focus();
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
    // Views: home (search landing), search (results or browse share one gallery), people, drive, settings.
    // "browse", "folders" and "prepare" remain accepted names for capture tooling.
    private void Navigate(string page)
    {
        _view = page switch { "browse" or "folders" => "search", "prepare" => "drive", "home" or "search" or "people" or "drive" or "settings" => page, _ => "home" };
        HomePage.Visibility = _view == "home" ? Visibility.Visible : Visibility.Collapsed;
        SearchPage.Visibility = _view == "search" ? Visibility.Visible : Visibility.Collapsed;
        PeoplePage.Visibility = _view == "people" ? Visibility.Visible : Visibility.Collapsed;
        DrivePage.Visibility = _view == "drive" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = _view == "settings" ? Visibility.Visible : Visibility.Collapsed;
        TitleSearchHost.Visibility = _view == "home" ? Visibility.Hidden : Visibility.Visible;
        FiltersPopup.IsOpen = false; if (_view != "search") ClosePreview();
        UpdateSearchMode(); ApplyResponsiveLayout();
    }
    private const string PersonEvidenceNotice = "A sampled face falls within this scene or passage. Check the face and surrounding footage: action, role and speaker are not verified.";
    private bool _hasPersonEvidence;
    private bool IsBrowsing => _submittedQuery.Length == 0;
    private bool HasNoFilters => _filters is { KindIndex: 0, DateIndex: 0, Folder: null, ExactOnly: false } && (_filters.PersonId is null || _filters.PersonId == Guid.Empty);
    // Results and Browse share the gallery: an empty query is newest-first browsing with a folder pane.
    private void UpdateSearchMode()
    {
        if (!_uiReady) return;
        var person = _filters.PersonId is Guid id && id != Guid.Empty ? (PersonFilter.SelectedItem as PersonChip)?.Name : null;
        SearchTitle.Text = !IsBrowsing ? $"Results for “{_submittedQuery}”" : person ?? (_filters.Folder is { } folder ? folder.Replace('\\', '/').Replace("/", " › ") : "All files");
        SearchSubtitle.Text = _hasPersonEvidence ? "People in these results are possible matches from sampled frames. Check each moment before relying on it."
            : !IsBrowsing ? (_boundedSearch ? "The closest matches, best first" : "Best matches first") : person is not null ? $"Files where {person} may appear, newest first" : "Newest first";
        SearchNav.IsChecked = _view == "home" || (_view == "search" && !IsBrowsing);
        BrowseNav.IsChecked = _view == "search" && IsBrowsing;
        PeopleNav.IsChecked = _view == "people";
        DriveNav.IsChecked = _view == "drive";
        SettingsNav.IsChecked = _view == "settings";
        FolderPane.Visibility = _view == "search" && IsBrowsing && ActualWidth >= 1100 ? Visibility.Visible : Visibility.Collapsed;
        FolderList.SelectedItem = _displayFolders.FirstOrDefault(f => f.RelativePath == _filters.Folder);
    }
    private void SearchNavClick(object sender, RoutedEventArgs e) { Navigate("home"); HeroQueryBox.Focus(); }
    private async void BrowseNavClick(object sender, RoutedEventArgs e) => await BrowseAsync();
    private async Task BrowseAsync(int? kind = null)
    {
        Navigate("browse");
        var needsSearch = !IsBrowsing || (CurrentTiles.Count == 0 && _status.FileCount > 0) || (kind is int k && k != KindFilter.SelectedIndex);
        if (kind is int index) { var initialized = _initialized; _initialized = false; KindFilter.SelectedIndex = index; _initialized = initialized; }
        if (!needsSearch) return;
        QueryBox.Clear(); await SearchAsync(true, submitQuery: true);
    }
    private async void PeopleNavClick(object sender, RoutedEventArgs e) { Navigate("people"); await LoadPeopleAsync(); }
    private void DriveNavClick(object sender, RoutedEventArgs e) => Navigate("drive");
    private void SettingsNavClick(object sender, RoutedEventArgs e) { Navigate("settings"); SynchronizeAppearanceMenu(); }
    private async void KindShortcutClick(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is string tag && int.TryParse(tag, out var kind)) await BrowseAsync(kind); }
    private async void RecentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ResultTile recent) return;
        if (!IsBrowsing || !HasNoFilters) { ResetFilterControls(); QueryBox.Clear(); Navigate("browse"); await SearchAsync(true, submitQuery: true); }
        else Navigate("browse");
        await SelectResultAsync(recent.Result);
    }
    private async void HomePersonClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PersonTile tile) await FindPersonAsync(tile.Person.Id);
    }
    private async Task FindPersonAsync(Guid personId)
    {
        Navigate("search"); var initialized = _initialized; _initialized = false;
        PersonFilter.SelectedItem = PersonFilter.Items.OfType<PersonChip>().FirstOrDefault(p => p.PersonId == personId);
        _initialized = initialized; await SearchAsync(true);
    }
    // Starter searches: names the user already gave, recent searches this session, then neutral examples.
    private void BuildSuggestions()
    {
        SuggestionPanel.Children.Clear();
        SuggestionPanel.Children.Add(new TextBlock { Text = "Try", Style = (Style)FindResource("CaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 8) });
        void Chip(string label, RoutedEventHandler click) { var chip = new Button { Content = label, Style = (Style)FindResource("SuggestionChip") }; chip.Click += click; SuggestionPanel.Children.Add(chip); }
        foreach (var query in _recentQueries.Take(3)) Chip(query, async (_, _) => { QueryBox.Text = query; Navigate("search"); await SearchAsync(true, submitQuery: true); });
        foreach (var person in _people.Where(p => p.Confirmed).Take(3)) Chip(person.Name, async (_, _) => await FindPersonAsync(person.Id));
        foreach (var example in new[] { "sunset at the beach", "birthday cake", "receipt or invoice", "someone talking about travel" })
        {
            if (SuggestionPanel.Children.Count > 5) break;
            Chip(example, async (_, _) => { QueryBox.Text = example; Navigate("search"); await SearchAsync(true, submitQuery: true); });
        }
    }

    // Recent searches travel with the drive. A read-only or busy drive only loses this convenience.
    private async Task SaveRecentQueriesAsync()
    {
        if (_backend is null || _closing) return;
        try { await _backend.SaveRecentQueriesAsync(_recentQueries.ToArray()); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
    // Folders and recent searches saved on this drive, applied when its library first opens.
    private void RestoreDrivePreferences()
    {
        if (_recentQueries.Count == 0 && _status.RecentQueries.Count > 0) { _recentQueries.AddRange(_status.RecentQueries.Take(6)); BuildSuggestions(); }
        if (_preparationFolders.Count == 0 && !_preparationUiBusy)
            foreach (var folder in _status.PreparationFolders) _preparationFolders.Add(folder.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task RefreshStatusAsync()
    {
        if (_backend is null || _statusPolling || _closing) return;
        _statusPolling = true;
        try
        {
            var previousLibrary = _status.LibraryId;
            var wasConnected = _status.Connected;
            var wasSemantic = _status.SemanticAvailable;
            // A sleeping or removed drive can block this check for seconds; keep it off the UI thread.
            _status = await Task.Run(() => Directory.Exists(_root)) ? await _backend.GetStatusAsync() : _status with { Connected = false, StatusDetail = "Drive disconnected." };
            if (previousLibrary != Guid.Empty && _status.LibraryId != Guid.Empty && previousLibrary != _status.LibraryId)
            {
                _searchCancellation?.Cancel(); ClosePreview(); CurrentTiles.Clear(); Rows.Clear(); _searchGeneration = null; _thumbnails.ReleaseFiles();
                _pages.Clear(); _pageLru.Clear(); _pageCursors.Clear(); _pageCursors.Add(null); _page = 0;
                _boundedSearch = false; SearchMoreButton.Visibility = Visibility.Collapsed;
                var initialized = _initialized; _initialized = false; PersonFilter.SelectedIndex = 0; _initialized = initialized; CaptureFilterState(); UpdateFilterUi();
                _recentQueries.Clear(); if (!_preparationUiBusy) _preparationFolders.Clear(); RestoreDrivePreferences(); BuildSuggestions();
                await LoadPeopleAsync();
                Notice("A different archive is connected. Run your search to see this drive's files. Reopen a person before editing their details.");
            }
            if (wasConnected != _status.Connected || wasSemantic != _status.SemanticAvailable) { _dismissedStatusNotice = null; if (_status.Connected) HideNotice(true); }
            UpdatePreparationControls();
            UpdateDriveSummary();
            if (!_displayFolders.SequenceEqual(_status.Folders))
            {
                _displayFolders = _status.Folders;
                FolderList.ItemsSource = _displayFolders; DriveFolderList.ItemsSource = _displayFolders;
                FolderList.SelectedItem = _displayFolders.FirstOrDefault(folder => folder.RelativePath == _filters.Folder);
            }
            FolderEmpty.Visibility = _status.Folders.Count == 0 && FolderPane.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            PreparedFoldersCard.Visibility = _status.Folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AllFoldersCount.Text = $"{_status.FileCount:N0}";
            // Rebinding resets scrolling and open dropdowns, so lists change only when their contents do.
            if (CapabilitiesList.ItemsSource is not IEnumerable<RuntimeCapability> capabilities || !capabilities.SequenceEqual(_status.Capabilities)) CapabilitiesList.ItemsSource = _status.Capabilities;
            if (IssuesList.ItemsSource is not IEnumerable<ArchiveIssue> issues || !issues.SequenceEqual(_status.Issues)) IssuesList.ItemsSource = _status.Issues;
            IssuesHeading.Text = _status.Issues.Count == 0 ? "No files need attention" : $"{_status.Issues.Count:N0} file{(_status.Issues.Count == 1 ? " needs" : "s need")} attention";
            var selectedFolder = (FolderFilter.SelectedItem as FolderChoice)?.Path ?? _filters.Folder;
            var choices = new[] { new FolderChoice("All folders", null) }.Concat(_status.Folders.Select(f => new FolderChoice(f.RelativePath, f.RelativePath))).ToArray();
            if (selectedFolder is not null && !choices.Any(f => f.Path == selectedFolder)) choices = choices.Append(new FolderChoice(selectedFolder, selectedFolder)).ToArray();
            if (FolderFilter.ItemsSource is not FolderChoice[] shown || !shown.SequenceEqual(choices))
            {
                _initialized = false;
                FolderFilter.ItemsSource = choices; FolderFilter.SelectedItem = choices.FirstOrDefault(f => f.Path == selectedFolder) ?? choices[0];
                _initialized = true;
            }
            if (!_status.Connected)
            {
                StopPlayback(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel();
                _thumbnails.ReleaseFiles(); Notice("Drive disconnected. Reconnect the same drive to continue. Your query is preserved.");
            }
            else if (_status.IsPreparing) Notice("Preparation is running. Search currently prepared files while the rest are processed.", false);
            else if (_status.FileCount > 0 && !_status.SemanticAvailable) Notice("Search by meaning is unavailable on this PC. You can still search file names and exact words. See Drive for details.", false);
            else if (!_searching) HideNotice();
            if (CurrentTiles.Count == 0) UpdateEmptyState();
            ApplyResponsiveLayout();
        }
        catch (Exception ex)
        {
            if (!Directory.Exists(_root))
            {
                _status = _status with { Connected = false };
                UpdateDriveSummary(); StopPlayback(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel();
            }
            Notice($"Archive unavailable: {ex.Message}");
        }
        finally { _statusPolling = false; }
    }

    // One status, shown in the title bar pill, the Drive page header, Home and the rail badge.
    private void UpdateDriveSummary()
    {
        var name = _status.DisplayName;
        var prepared = _status.FileCount > 0;
        var preparing = _status.IsPreparing || _preparationUiBusy;
        DriveName.Text = DriveTitle.Text = name;
        ConnectionLabel.Text = _backend is null ? "Opening" : !_status.Connected ? "Disconnected" : preparing ? "Preparing" : prepared ? "Ready" : "Not prepared";
        var dot = !_status.Connected || _backend is null ? "MutedBrush" : preparing ? "AccentBrush" : prepared ? "ReadyBrush" : "WarningBrush";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        DriveStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        PreparingBadge.Visibility = preparing ? Visibility.Visible : Visibility.Collapsed;
        var preparedOn = _status.PreparedUtc is DateTimeOffset at ? $" · prepared {at.ToLocalTime():d MMM yyyy}" : "";
        DriveStatusText.Text = !_status.Connected ? "Disconnected. Reconnect this drive to continue." : preparing ? "Preparing. Search keeps working with prepared files." : prepared ? $"Ready to search · {_status.FileCount:N0} files{preparedOn}" : "Not prepared yet";
        FooterStatus.Text = !_status.Connected ? "Reconnect this drive to continue." : $"{_status.FileCount:N0} files · {_status.SegmentCount:N0} searchable moments · {FormatBytes(_status.TotalBytes)}";
        HomeSubtitle.Text = $"Describe a moment, name a person, or type words you remember. Everything stays on {name}.";
        QueryPlaceholder.Text = $"Search {name}";
        HomeSetupCard.Visibility = !prepared || !_status.Connected ? Visibility.Visible : Visibility.Collapsed;
        HomeSetupTitle.Text = !_status.Connected ? "This drive is disconnected" : "Make this drive searchable";
        HomeSetupText.Text = !_status.Connected ? "Reconnect the same drive to pick up where you left off. Your search is kept." : "FindIt reads your folders once and saves a private search index on the drive. Your files are not moved or changed.";
        HomeSetupButton.Visibility = _status.Connected ? Visibility.Visible : Visibility.Collapsed;
        HomeLibrarySections.Visibility = prepared && _status.Connected ? Visibility.Visible : Visibility.Collapsed;
        WelcomeCard.Visibility = !prepared && _status.Connected && !preparing && _backend is not null ? Visibility.Visible : Visibility.Collapsed;
        string Count(FileKind kind, string unit, string fallback) => _status.Kinds.FirstOrDefault(k => k.Kind == kind) is { } count ? $"{count.Count:N0} {unit}{(count.Count == 1 ? "" : "s")}" : fallback;
        PhotoCount.Text = Count(FileKind.Image, "photo", "Browse by date");
        VideoCount.Text = Count(FileKind.Video, "video", "With matching moments");
        DocumentCount.Text = Count(FileKind.Document, "document", "PDF, Word, Excel, text");
        AudioCount.Text = Count(FileKind.Audio, "recording", "Speech is searchable");
        StartPreparationButton.Content = _preparationPaused ? "Resume" : prepared ? "Update index" : "Prepare drive";
        PrepareFoldersTitle.Text = prepared ? "Prepare more folders" : "Folders to prepare";
        PrepareHint.Text = prepared ? "Unchanged files are skipped, so updates are quick." : "Takes a while for large drives. You can pause and resume.";
    }

    private SearchFilters ReadFilters() => _filters.ToSearchFilters(DateTimeOffset.Now, TimeZoneInfo.Local);

    // Results arrive in pages of 256. Scrolling near the end appends the next page, up to
    // AutoLoadedPages at once; Previous and Next then move past what is loaded.
    private const int PageSize = 256, AutoLoadedPages = 16;
    private int _firstPage;
    private CancellationTokenSource? _appendCancellation;
    private long _loadMoreFailedAt;
    private bool CanLoadMore => !_searching && !_closing && _backend is not null && _page - _firstPage + 1 < AutoLoadedPages
        && _pageCursors.Count > _page + 1 && (_loadMoreFailedAt == 0 || Stopwatch.GetElapsedTime(_loadMoreFailedAt) > TimeSpan.FromSeconds(5));
    private async Task LoadMoreAsync() { if (CanLoadMore) await SearchAsync(false, append: true); }
    private async void ResultsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The list's column header has its own scroll viewer; only the outer one shows results.
        if (sender is DependencyObject owner && e.OriginalSource is ScrollViewer scroll && ReferenceEquals(scroll, VisualChildren<ScrollViewer>(owner).FirstOrDefault()) && NearEnd(scroll)) await LoadMoreAsync();
    }

    private async Task SearchAsync(bool reset, int? targetPage = null, bool submitQuery = false, bool deepen = false, bool append = false, bool keepPreview = false)
    {
        if (await SearchPageAsync(reset, targetPage, submitQuery, deepen, append, keepPreview, restartSilently: true) is int restoreCount and > 0)
        {
            // Preparation publishing new files, a people correction or an expired session
            // invalidates the ranked window. Run the same search again, once, where the user was.
            await SearchPageAsync(true, null, false, true, false, true, restartSilently: false);
            while (CurrentTiles.Count > 0 && CurrentTiles.Count < restoreCount && CanLoadMore) await SearchPageAsync(false, null, false, false, true, true, restartSilently: false);
            Notice("The library changed while you were searching, so the results were refreshed.", false);
        }
        else if (!_closing) _ = Dispatcher.BeginInvoke(async () => { if (GalleryNeedsMore()) await LoadMoreAsync(); }, DispatcherPriority.Loaded);
    }
    /// <returns>When the worker asked for a restart that should happen silently, how many results were shown.</returns>
    private async Task<int> SearchPageAsync(bool reset, int? targetPage, bool submitQuery, bool deepen, bool append, bool keepPreview, bool restartSilently)
    {
        if (_backend is null || _closing) return 0;
        CaptureFilterState();
        if (_filters.ValidateDateRange() is { } validation) { Notice(validation); return 0; }
        UpdateFilterUi();
        CancellationTokenSource cancellation;
        // An appended page must not cancel the thumbnails of the pages already shown.
        if (append) cancellation = _appendCancellation = CancellationTokenSource.CreateLinkedTokenSource(_searchCancellation?.Token ?? CancellationToken.None);
        else { _appendCancellation = null; _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); cancellation = _searchCancellation = new(); }
        if (reset)
        {
            if (!deepen) _searchBudget = null;
            _boundedSearch = false;
            _page = _firstPage = 0; _pageCursors.Clear(); _pageCursors.Add(null); _pages.Clear(); _pageLru.Clear(); _searchGeneration = null; _loadMoreFailedAt = 0;
            if (!keepPreview) ClosePreview();
            if (submitQuery) _submittedQuery = QueryBox.Text.Trim();
            _submittedFilters = ReadFilters();
            if (submitQuery && _submittedQuery.Length > 0 && !string.Equals(_recentQueries.FirstOrDefault(), _submittedQuery, StringComparison.Ordinal))
            {
                _recentQueries.RemoveAll(q => string.Equals(q, _submittedQuery, StringComparison.OrdinalIgnoreCase)); _recentQueries.Insert(0, _submittedQuery); if (_recentQueries.Count > 6) _recentQueries.RemoveAt(6); BuildSuggestions();
                _ = SaveRecentQueriesAsync();
            }
            UpdateSearchMode();
        }
        var requestedPage = append ? _page + 1 : targetPage ?? _page;
        _searchError = null;
        _searching = true; SearchProgress.Visibility = Visibility.Visible;
        if (!append) ResultCount.Text = "Searching…";
        PreviousPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        SearchMoreButton.IsEnabled = false;
        if (CurrentTiles.Count == 0) UpdateEmptyState();
        try
        {
            // Even a previously viewed page validates its worker session. Cached page
            // metadata must not bypass expiry, generation changes or people corrections.
            var response = await _backend.SearchAsync(new() { Query = _submittedQuery, Filters = _submittedFilters, Limit = PageSize,
                Cursor = _pageCursors.ElementAtOrDefault(requestedPage), GenerationId = _searchGeneration, RetrievalBudget = _searchBudget }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (response.RestartRequired)
            {
                if (restartSilently && response.GenerationId > 0) return Math.Max(CurrentTiles.Count, PageSize);
                _page = _firstPage = 0; _pageCursors.Clear(); _pageCursors.Add(null); _pages.Clear(); _pageLru.Clear(); _searchGeneration = null;
                ClosePreview(); CurrentTiles.Clear(); Rows.Clear();
                _searchError = string.IsNullOrWhiteSpace(response.Notice) ? "Run the search again." : response.Notice;
                ResultCount.Text = "Search needs restarting"; Notice(_searchError);
                SearchMoreButton.Visibility = Visibility.Collapsed;
                return 0;
            }
            _searchGeneration = response.GenerationId;
            _boundedSearch = response.IsBoundedWindow;
            if (response.PageCursor is not null) _pageCursors[requestedPage] = response.PageCursor;
            if (!_pages.ContainsKey(requestedPage)) _pageLru.Enqueue(requestedPage);
            _pages[requestedPage] = response;
            while (_pages.Count > 8 && _pageLru.TryDequeue(out var oldest)) _pages.Remove(oldest);
            if (response.NextCursor is not null && _pageCursors.Count == requestedPage + 1) _pageCursors.Add(response.NextCursor);
            if (append) _page = requestedPage;
            else
            {
                if (_page != requestedPage || _firstPage != requestedPage) ClosePreview();
                _page = _firstPage = requestedPage;
                CurrentTiles.Clear();
            }
            foreach (var result in response.Results) CurrentTiles.Add(new ResultTile { Result = result, IsSelected = result.FileId == _selectedResult?.FileId });
            ArrangeRows(true);
            if (!append && !keepPreview) GalleryScrollToTop();
            if (_page == 0 && IsBrowsing && HasNoFilters)
            {
                RecentTiles.Clear();
                foreach (var result in response.Results.Take(12)) RecentTiles.Add(new ResultTile { Result = result, TileWidth = 148, ThumbnailHeight = 111 });
            }
            EmptyState.Visibility = CurrentTiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var first = _firstPage * PageSize + 1; var last = _firstPage * PageSize + CurrentTiles.Count;
            ResultCount.Text = CurrentTiles.Count == 0 ? "No results" : $"{first:N0}–{last:N0}{(response.TotalCount is long total ? $" of {total:N0}" : "")}{(response.NextCursor is not null && response.TotalCount is null ? " · More results available" : "")}";
            if (response.IsBoundedWindow) ResultCount.Text += " · Closest matches";
            UpdateSearchMode();
            SearchMoreButton.Visibility = response.CanSearchDeeper ? Visibility.Visible : Visibility.Collapsed;
            SearchMoreButton.IsEnabled = response.CanSearchDeeper;
            SearchMoreButton.Tag = response.RetrievalBudget;
            PreviousPageButton.IsEnabled = _firstPage > 0; NextPageButton.IsEnabled = response.NextCursor is not null;
            // Person evidence is qualified beside the results and in the preview, so the
            // worker's matching caveat is not repeated in the page-wide notice.
            _hasPersonEvidence = CurrentTiles.Any(t => t.Result.MatchCategory == MatchCategory.PossiblePersonAction || t.Result.Moments.Any(m => m.PersonEvidence.Count > 0));
            var notice = response.Notice?.Replace(PersonEvidenceNotice, "", StringComparison.Ordinal).Trim();
            if (!string.IsNullOrWhiteSpace(notice)) Notice(notice); else if (!_status.IsPreparing) HideNotice(true);
            UpdateSearchMode();
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
                    if (append) _loadMoreFailedAt = Stopwatch.GetTimestamp();
                    PreviousPageButton.IsEnabled = _firstPage > 0;
                    NextPageButton.IsEnabled = _pages.TryGetValue(_page, out var visiblePage) && visiblePage.NextCursor is not null;
                    SearchMoreButton.IsEnabled = visiblePage?.CanSearchDeeper == true;
                    if (!append) ResultCount.Text = $"Page {_page + 1:N0} · Could not load that page. Try again.";
                }
            }
        }
        finally
        {
            if (append ? ReferenceEquals(cancellation, _appendCancellation) : ReferenceEquals(cancellation, _searchCancellation))
            {
                _searching = false; SearchProgress.Visibility = Visibility.Collapsed;
                if (CurrentTiles.Count == 0) UpdateEmptyState();
            }
            if (append) { if (ReferenceEquals(cancellation, _appendCancellation)) _appendCancellation = null; cancellation.Dispose(); }
        }
        return 0;
    }
    // A tall window can show every loaded row without scrolling, so no scroll event asks for more.
    private bool GalleryNeedsMore()
    {
        var scroll = VisualChildren<ScrollViewer>(_listMode ? MetadataList : Gallery).FirstOrDefault();
        return scroll is not null && scroll.IsVisible && NearEnd(scroll);
    }
    // Within one and a half screens of the end. The gallery scrolls by pixels and the list by
    // items, so the distance is measured in viewports; an unmeasured layout never counts.
    private static bool NearEnd(ScrollViewer scroll) => scroll.ViewportHeight > 0 && scroll.ExtentHeight > 0 && scroll.ScrollableHeight - scroll.VerticalOffset <= scroll.ViewportHeight * 1.5;

    private async void TileLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResultTile tile || tile.Thumbnail is not null || _thumbnailRequests.ContainsKey(tile)) return;
        await LoadThumbnailAsync(tile, VisualTreeHelper.GetDpi((Visual)sender));
    }
    // A tile resized in place keeps its image; ask again so a larger box gets enough pixels.
    private async void RefreshGrownThumbnail(ResultTile tile)
    {
        if (tile.Thumbnail is null || _thumbnailRequests.ContainsKey(tile)) return;
        await LoadThumbnailAsync(tile, VisualTreeHelper.GetDpi(this));
    }
    private async Task LoadThumbnailAsync(ResultTile tile, DpiScale dpi)
    {
        // Recent files on Home outlive searches, so only gallery tiles follow search cancellation.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(RecentTiles.Contains(tile) ? CancellationToken.None : _searchCancellation?.Token ?? CancellationToken.None);
        _thumbnailRequests[tile] = cancellation;
        try
        {
            var thumbnail = await _thumbnails.ReadAsync(tile.Result.Thumbnail, cancellation.Token, new Size(tile.TileWidth * dpi.DpiScaleX, tile.ThumbnailHeight * dpi.DpiScaleY));
            if (!cancellation.IsCancellationRequested && !ReferenceEquals(tile.Thumbnail, thumbnail)) tile.Thumbnail = thumbnail;
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
        var noMatches = _status.Connected && !_searching && _searchError is null && prepared;
        EmptyTitle.Text = !_status.Connected ? "Your drive is disconnected" : _searching ? "Searching your archive" : _searchError is not null ? "Run your search again" : prepared ? (_submittedQuery.Length > 0 ? $"No matches for “{_submittedQuery}”" : "Nothing here with these filters") : "Your archive, ready to discover";
        EmptyDescription.Text = !_status.Connected ? "Reconnect this drive to pick up where you left off." : _searching ? "Finding files that match your search." : _searchError is not null ? "Try again. Your search and filters are still here." : prepared ? "Your filters may be narrowing things down. Try one of these:" : "Choose folders to make your photos, videos and documents searchable offline.";
        EmptyGlyph.Text = !_status.Connected ? "" : _searchError is not null ? "" : prepared ? "" : "";
        EmptyAction.Content = _searchError is not null ? "Try search again" : prepared ? "Clear search and filters" : "Prepare this drive";
        EmptyAction.Visibility = _searching ? Visibility.Collapsed : Visibility.Visible;
        EmptyAction.IsEnabled = _status.Connected && _backend is not null;
        EmptyState.Visibility = Visibility.Visible;
        // Concrete next steps for an empty result, each undoing one narrowing choice.
        EmptySuggestions.Children.Clear();
        void Suggest(string text, string action, Func<Task> run)
        {
            var row = new DockPanel { Margin = new Thickness(16, 10, 12, 10) };
            var button = new Button { Content = action, FontSize = 13, MinHeight = 30, Padding = new Thickness(12, 0, 12, 0) };
            button.Click += async (_, _) => await run();
            DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            if (EmptySuggestions.Children.Count > 0) { var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); EmptySuggestions.Children.Add(line); }
            EmptySuggestions.Children.Add(row);
        }
        if (noMatches)
        {
            if (_filters.ExactOnly) Suggest("Search by meaning instead of exact words", "Turn off Exact words", async () => { ExactFilter.IsChecked = false; await Task.CompletedTask; });
            if (_filters.KindIndex > 0) Suggest("Include every type of file", "Show all types", async () => { KindFilter.SelectedIndex = 0; await Task.CompletedTask; });
            if (_filters.DateIndex > 0 || _filters.Folder is not null || _filters.PersonId is Guid person && person != Guid.Empty)
                Suggest("Remove the date, folder and person filters", "Clear filters", async () => { var kind = KindFilter.SelectedIndex; var exact = ExactFilter.IsChecked; ResetFilterControls(); var initialized = _initialized; _initialized = false; KindFilter.SelectedIndex = kind; ExactFilter.IsChecked = exact; _initialized = initialized; await SearchAsync(true); });
            if (_boundedSearch && SearchMoreButton.Tag is int) Suggest("Look past the closest matches", "Search deeper", async () => await SearchDeeperAsync());
        }
        EmptySuggestionsCard.Visibility = EmptySuggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptySuggestions.Children.Count == 0 && noMatches) EmptyDescription.Text = "Try a broader description, or words you remember from the file.";
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
        MomentsList.ItemsSource = null; MomentsHeading.Visibility = Visibility.Collapsed; MomentMarkers.Children.Clear(); _markedDuration = 0;
        PreviewName.Text = result.Name; PreviewPath.Text = result.RelativePath.Replace('\\', '/').Replace("/", " › "); SetPreviewText(result.Snippet);
        PreviewMediaRow.Height = new GridLength(200);
        PreviewImage.Source = null; PreviewPlaceholder.Text = "Loading preview…"; PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPanel.Visibility = Visibility.Visible; ApplyResponsiveLayout(); UpdateLayout();
        var tileKind = CurrentTiles.FirstOrDefault(t => t.Result.FileId == result.FileId)?.KindLabel ?? result.Kind.ToString();
        PreviewDetails.Text = $"{tileKind} · {FormatBytes(result.Bytes)}";
        PreviewDate.Text = result.ModifiedUtc.ToLocalTime().ToString("d MMMM yyyy, HH:mm");
        var index = CurrentTiles.ToList().FindIndex(t => t.Result.FileId == result.FileId);
        PreviewPreviousButton.IsEnabled = index > 0; PreviewNextButton.IsEnabled = index >= 0 && index < CurrentTiles.Count - 1;
        PossibleMatchBanner.Visibility = result.MatchCategory == MatchCategory.PossiblePersonAction ? Visibility.Visible : Visibility.Collapsed;
        PersonEvidenceDetails.Visibility = Visibility.Collapsed;
        PersonEvidenceFaces.Visibility = Visibility.Collapsed; PersonEvidenceFaces.ItemsSource = null;
        EvidencePortrait.Source = null; EvidencePortrait.Visibility = Visibility.Collapsed;
        try
        {
            var preview = await _backend.GetPreviewAsync(ResultPreview.Request(result, segmentId), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); _preview = preview;
            SetPreviewText(string.IsNullOrWhiteSpace(preview.Text) ? result.Snippet : preview.Text);
            var moments = result.MatchedPersonIds.Count > 0 ? result.Moments : preview.Moments.Count > 0 ? preview.Moments : result.Moments;
            var showMoments = moments.Count > 1 || result.Kind is FileKind.Video or FileKind.Audio;
            MomentsList.ItemsSource = showMoments ? moments : [];
            MomentsHeading.Visibility = showMoments ? Visibility.Visible : Visibility.Collapsed;
            MomentsHeading.Text = moments.Count == 1 ? "Matching moment" : $"{moments.Count:N0} matching moments";
            _markerMoments = result.Kind is FileKind.Video or FileKind.Audio ? moments : [];
            if (preview.PersonEvidence.Count > 0)
            {
                // The faces list and the possible-match note already explain the evidence;
                // this line only appears when an older library lacks the context frame.
                PersonEvidenceDetails.Text = ""; PersonEvidenceDetails.Visibility = Visibility.Collapsed;
                var faceTiles = new List<EvidenceFaceTile>();
                foreach (var evidence in preview.PersonEvidence)
                {
                    var portrait = await _thumbnails.ReadAsync(evidence.Portrait, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var timestamp = evidence.StartMs is long sample ? $"At {ClockFormat.Format(sample)} · " : "";
                    faceTiles.Add(new(evidence.Name, timestamp + (evidence.Confirmed ? "Confirmed match" : "Possible match"), portrait));
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
        // Docking the preview narrows the gallery; moving between files does not.
        ApplyResponsiveLayout();
        RequestArrangeRows();
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
            { PersonEvidenceDetails.Text = "Showing saved face crop; the surrounding frame is unavailable in this older library."; PersonEvidenceDetails.Visibility = Visibility.Visible; }
            VideoPlayer.Width = VideoPlayer.Height = audioOnly ? 1 : double.NaN;
            VideoPlayer.HorizontalAlignment = audioOnly ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            VideoPlayer.VerticalAlignment = audioOnly ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            VideoPlayer.Visibility = _showingPreparedFrame ? Visibility.Hidden : Visibility.Visible; VideoControls.Visibility = Visibility.Visible;
            PreviewPlaceholder.Text = audioOnly ? "♪\nAudio preview" : "Loading preview…";
            PreviewPlaceholder.Visibility = audioOnly ? Visibility.Visible : Visibility.Collapsed;
            VideoPlayer.UpdateLayout();
            _player ??= new PreviewPlayerClient(_root, VideoPlayer.WindowHandle);
            PlayPauseButton.Content = ""; AutomationProperties.SetName(PlayPauseButton, "Play playback");
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
    private void PreviewScrimClick(object sender, MouseButtonEventArgs e) => ClosePreview(true);
    private async void PreviewPreviousClick(object sender, RoutedEventArgs e) => await StepPreviewAsync(-1);
    private async void PreviewNextClick(object sender, RoutedEventArgs e) => await StepPreviewAsync(1);
    private async Task StepPreviewAsync(int direction)
    {
        var index = CurrentTiles.ToList().FindIndex(t => t.Result.FileId == _selectedResult?.FileId);
        if (index < 0 || index + direction < 0 || index + direction >= CurrentTiles.Count) return;
        var tile = CurrentTiles[index + direction];
        if (!_listMode && Rows.ElementAtOrDefault(ContactSheetLayout.GetRowIndex(_sheetRows, index + direction)) is { } row) Gallery.ScrollIntoView(row);
        if (_listMode) { MetadataList.SelectedItem = tile; MetadataList.ScrollIntoView(tile); }
        else await SelectResultAsync(tile.Result);
    }
    private void CopyPathClick(object sender, RoutedEventArgs e)
    {
        try { var path = _preview?.OriginalPath ?? (_selectedResult is null ? null : SafePath(_selectedResult.RelativePath)); if (path is not null) { Clipboard.SetText(path); Notice("File path copied.", false); } }
        catch (Exception ex) { Notice($"Could not copy the path: {ex.Message}"); }
    }
    // Document text with the submitted words highlighted, so the match is visible at a glance.
    private void SetPreviewText(string text)
    {
        _previewPlainText = text ?? "";
        PreviewText.Inlines.Clear();
        PreviewText.Visibility = string.IsNullOrWhiteSpace(_previewPlainText) ? Visibility.Collapsed : Visibility.Visible;
        if (_previewPlainText.Length == 0) return;
        var terms = _submittedQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(term => term.Length >= 3).Select(System.Text.RegularExpressions.Regex.Escape).ToArray();
        if (terms.Length == 0) { PreviewText.Inlines.Add(new System.Windows.Documents.Run(_previewPlainText)); return; }
        var pattern = new System.Text.RegularExpressions.Regex(string.Join("|", terms), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var position = 0;
        try
        {
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(_previewPlainText))
            {
                if (match.Index > position) PreviewText.Inlines.Add(new System.Windows.Documents.Run(_previewPlainText[position..match.Index]));
                var highlight = new System.Windows.Documents.Run(match.Value) { FontWeight = FontWeights.SemiBold };
                highlight.SetResourceReference(System.Windows.Documents.TextElement.BackgroundProperty, "WarningTintBrush");
                PreviewText.Inlines.Add(highlight); position = match.Index + match.Length;
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
        if (position < _previewPlainText.Length) PreviewText.Inlines.Add(new System.Windows.Documents.Run(_previewPlainText[position..]));
    }
    private void ClosePreview(bool restoreFocus = false) { _previewCancellation?.Cancel(); StopPlayback(); _preview = null; _selectedResult = null; MetadataList.SelectedItem = null; foreach (var tile in CurrentTiles) tile.IsSelected = false; ApplyResponsiveLayout(); RequestArrangeRows(); if (restoreFocus) RestoreResultFocus(); }
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
        _preparationUiBusy = true; _preparationPaused = false; UpdatePreparationControls(); UpdateDriveSummary(); ProgressCard.Visibility = Visibility.Visible; ProgressMessage.Text = "";
        var progress = new Progress<PreparationProgress>(UpdateProgress);
        var request = new PreparationRequest { Folders = _preparationFolders.ToArray(), VerifyContents = VerifyContents.IsChecked == true, OcrLanguages = OcrLanguages.SelectedIndex switch { 1 => ["eng"], 2 => ["ara"], _ => ["eng", "ara"] } };
        _preparationTask = PrepareCoreAsync(request, progress, _prepareCancellation.Token);
        await _preparationTask;
    }
    private async Task PrepareCoreAsync(PreparationRequest request, IProgress<PreparationProgress> progress, CancellationToken token)
    {
        try { _status = await _backend!.PrepareAsync(request, progress, token); Notice("Preparation complete. Your updated archive is ready to search."); await RefreshStatusAsync(); await LoadPeopleAsync(); }
        catch (OperationCanceledException) { _preparationPaused = true; ProgressStage.Text = "Paused"; ProgressMessage.Text = "Finished files are saved. Choose Resume to continue."; UpdateStageStrip(null, paused: true); }
        catch (Exception ex) { ProgressStage.Text = "Preparation stopped"; Notice($"Preparation could not continue: {ex.Message}"); }
        finally { _progressTimer.Stop(); _pendingProgress = null; _shownStage = null; _preparationUiBusy = false; UpdatePreparationControls(); UpdateDriveSummary(); }
    }
    // The job's phases. Each file is read, transcribed and embedded together, so those are one phase.
    private static readonly string[] StageNames = ["Finding files", "Reading and indexing", "Matching people", "Saving"];
    private int _stageIndex;
    private void BuildStageStrip()
    {
        StageStrip.Children.Clear();
        foreach (var name in StageNames)
        {
            var stage = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var bar = new Border { Height = 3, CornerRadius = new CornerRadius(2) }; bar.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            var label = new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }; label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stage.Children.Add(bar); stage.Children.Add(label); StageStrip.Children.Add(stage);
        }
    }
    private void UpdateStageStrip(int? current, bool paused = false)
    {
        if (current is int index) _stageIndex = index;
        for (var i = 0; i < StageStrip.Children.Count; i++)
        {
            if (StageStrip.Children[i] is not StackPanel { Children: [Border bar, TextBlock label] }) continue;
            var done = i < _stageIndex; var active = i == _stageIndex;
            bar.SetResourceReference(Border.BackgroundProperty, done || active ? (active && paused ? "WarningBrush" : "AccentBrush") : "TrackBrush");
            label.SetResourceReference(TextBlock.ForegroundProperty, done || active ? "InkBrush" : "MutedBrush");
            label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
    private void UpdatePreparationControls()
    {
        var available = _backend is not null && _status.Connected && !_closing;
        var busy = _preparationUiBusy || _status.IsPreparing;
        StartPreparationButton.IsEnabled = available && !busy;
        PausePreparationButton.IsEnabled = available && _preparationUiBusy && _prepareCancellation?.IsCancellationRequested != true;
        foreach (var control in new Control[] { AddPreparationFolderButton, RemovePreparationFolderButton, PreparationFolders, OcrLanguages, VerifyContents }) control.IsEnabled = available && !busy;
    }
    // Unchanged files can report thousands of times a second. Show the latest state at most
    // about ten times a second, but never hold back a change of phase.
    private PreparationProgress? _pendingProgress;
    private PreparationStage? _shownStage;
    private long _progressShownAt;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private void UpdateProgress(PreparationProgress progress)
    {
        if (progress.Stage == _shownStage && Stopwatch.GetElapsedTime(_progressShownAt) < _progressTimer.Interval)
        { _pendingProgress = progress; if (!_progressTimer.IsEnabled) _progressTimer.Start(); return; }
        ShowProgress(progress);
    }
    private void ShowPendingProgress() { _progressTimer.Stop(); if (_pendingProgress is { } latest) ShowProgress(latest); }
    private void ShowProgress(PreparationProgress progress)
    {
        _progressTimer.Stop(); _pendingProgress = null; _shownStage = progress.Stage; _progressShownAt = Stopwatch.GetTimestamp();
        ProgressCard.Visibility = Visibility.Visible;
        var percent = progress.Fraction is double fraction ? $" · {Math.Clamp(fraction, 0, 1):P0}" : "";
        ProgressStage.Text = progress.Stage switch { PreparationStage.Discovering => "Finding files", PreparationStage.Extracting or PreparationStage.Embedding or PreparationStage.Transcribing => "Reading and indexing your files", PreparationStage.RecognizingPeople => "Finding familiar faces", PreparationStage.Publishing => "Saving this update", PreparationStage.Completed => "Ready to travel", PreparationStage.Paused => "Paused", _ => "Preparing your files" } + (progress.Stage is PreparationStage.Completed or PreparationStage.Paused ? "" : percent);
        UpdateStageStrip(progress.Stage switch { PreparationStage.Discovering => 0, PreparationStage.Extracting or PreparationStage.Embedding or PreparationStage.Transcribing => 1, PreparationStage.RecognizingPeople => 2, PreparationStage.Publishing => 3, PreparationStage.Completed => 4, _ => null }, progress.Stage == PreparationStage.Paused);
        PreparationProgressBar.IsIndeterminate = progress.Fraction is null; PreparationProgressBar.Value = Math.Clamp(progress.Fraction ?? 0, 0, 1);
        var left = progress.SecondsRemaining is long seconds && progress.Stage is PreparationStage.Extracting or PreparationStage.Embedding or PreparationStage.Transcribing ? " · " + FormatRemaining(seconds) : "";
        ProgressSummary.Text = $"{progress.Completed:N0} processed · {progress.Unchanged:N0} unchanged · {progress.Failed:N0} needing attention{left}";
        ProgressPath.Text = progress.CurrentPath; ProgressMessage.Text = progress.Message;
        FooterStatus.Text = $"Preparing · {progress.Discovered:N0} files found · {progress.Segments:N0} searchable moments{left}";
    }
    private static string FormatRemaining(long seconds) => seconds < 90 ? "about a minute left"
        : seconds < 90 * 60 ? $"about {Math.Round(seconds / 60d):N0} minutes left"
        : $"about {seconds / 3600d:N1} hours left";
    private void PausePreparationClick(object sender, RoutedEventArgs e) { _prepareCancellation?.Cancel(); PausePreparationButton.IsEnabled = false; ProgressMessage.Text = "Pausing after current work is safely checkpointed…"; }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { var box = HomePage.IsVisible ? HeroQueryBox : QueryBox; box.Focus(); box.SelectAll(); e.Handled = true; }
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
    private static string FormatTime(long milliseconds) => ClockFormat.Format(milliseconds);
    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):N1} GB" : value >= 1024L * 1024 ? $"{value / (1024d * 1024):N1} MB" : value >= 1024 ? $"{value / 1024d:N1} KB" : $"{value:N0} B";
    private void EjectClick(object sender, RoutedEventArgs e)
    {
        if (!ThemedDialog.Confirm(this, "FindIt closes and Windows safely removes the drive. Your search index and people stay on the drive for next time.", $"Close FindIt and eject {_status.DisplayName}?", "Close and _eject")) return;
        _ejectRequested = true; Close();
    }
    // Custom title bar: caption buttons and maximized-window insets.
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindowClick(object sender, RoutedEventArgs e) => Close();
    private void WindowStateChanged(object? sender, EventArgs e)
    {
        // A maximized chrome-less window extends past the monitor by the resize frame.
        RootLayout.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        if (WindowState != WindowState.Minimized && _initialized) _ = RefreshStatusAsync();
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        AutomationProperties.SetName(MaximizeButton, WindowState == WindowState.Maximized ? "Restore" : "Maximize");
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
        _closing = true; _thumbnails.ReleaseFiles(); foreach (var request in _portraitRequests.Values.ToArray()) request.Cancel(); DisposeAppearance(); StopPlaybackPolling(); _statusTimer.Stop(); _searchCancellation?.Cancel(); _previewCancellation?.Cancel(); _prepareCancellation?.Cancel(); _updateCancellation?.Cancel();
        FooterStatus.Text = "Closing archive and releasing this drive…"; ConnectionLabel.Text = "Closing…";
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
