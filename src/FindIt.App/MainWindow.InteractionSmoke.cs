using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FindIt.Shared;

namespace FindIt.App;

public partial class MainWindow
{
    /// <summary>Explicit release QA: drives real WPF controls using an isolated, in-memory archive.</summary>
    public async Task RunInteractionSmokeAsync(string reportPath)
    {
        var checks = new List<InteractionCheck>();
        var watch = Stopwatch.StartNew();
        Exception? failure = null;
        var backend = new InteractionArchive(_root);
        void Check(string name, bool passed, object? details = null)
        {
            checks.Add(new(name, passed, details));
            if (!passed) throw new InvalidOperationException(name + " failed.");
        }
        async Task Idle()
        {
            var deadline = Stopwatch.StartNew();
            do
            {
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(20);
                if (deadline.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("WPF interaction did not settle.");
            } while (_searching);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        async Task Click(ButtonBase button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); await Idle(); }
        async Task RemoveChip(string key)
        {
            var chip = ActiveFilterPanel.Children.OfType<Button>().Single(b => Equals(b.Tag, key));
            await Click(chip);
            Check("remove-" + key + "-chip", !ActiveFilterPanel.Children.OfType<Button>().Any(b => Equals(b.Tag, key)));
        }
        KeyEventArgs KeyArgs(Key key) => new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor((FrameworkElement)Content).TransformBounds(new Rect(element.RenderSize));
        void LayoutCheck(string label, params FrameworkElement[] elements)
        {
            var surface = (FrameworkElement)Content;
            var bounds = elements.Select(element => new { element.Name, element.IsVisible, element.ActualWidth, element.ActualHeight, Bounds = Bounds(element) }).ToArray();
            Check(label, bounds.All(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0 && b.Bounds.Left >= -1 && b.Bounds.Top >= -1 && b.Bounds.Right <= surface.ActualWidth + 1 && b.Bounds.Bottom <= surface.ActualHeight + 1),
                bounds.Select(b => new { b.Name, b.IsVisible, b.ActualWidth, b.ActualHeight, left = b.Bounds.Left, top = b.Bounds.Top, right = b.Bounds.Right, bottom = b.Bounds.Bottom }));
        }
        try
        {
            Width = 1400; Height = 900;
            await AttachBackendAsync(backend); _statusTimer.Stop(); await Idle();
            Check("initial-prepared-library", SearchPage.IsVisible && CurrentTiles.Count == 256 && _people.Count == 40 && _searchGeneration == 42 && NextPageButton.IsEnabled && ResultCount.Text.Contains("650"));
            LayoutCheck("regular-library-layout", QueryBox, SearchHeadingRow, AllTab, AudioTab, FiltersButton, Gallery, PaginationBar);
            Check("tabs-and-filters-do-not-overlap", Bounds(AudioTab).Right <= Bounds(FiltersButton).Left);

            await Click(PhotosTab);
            Check("photos-tab-search-and-selection", _filters.KindIndex == 1 && PhotosTab.IsChecked == true && AllTab.IsChecked == false && backend.Requests.Last().Filters.Kind == FileKind.Image && CurrentTiles.All(t => t.Result.Kind == FileKind.Image));
            await Click(VideosTab); Check("videos-tab", backend.Requests.Last().Filters.Kind == FileKind.Video && VideosTab.IsChecked == true);
            await Click(DocumentsTab); Check("documents-tab", backend.Requests.Last().Filters.Kind == FileKind.Document && DocumentsTab.IsChecked == true);
            await Click(AudioTab); Check("audio-tab", backend.Requests.Last().Filters.Kind == FileKind.Audio && AudioTab.IsChecked == true);
            await Click(AllTab);

            ExactFilter.IsChecked = true; await Idle();
            Check("exact-filter", backend.Requests.Last().Filters.ExactOnly); await RemoveChip("exact");
            PersonFilter.SelectedIndex = 7; await Idle();
            var selectedPerson = ((PersonChip)PersonFilter.SelectedItem).PersonId;
            FolderFilter.SelectedIndex = 2; await Idle();
            var selectedFolder = ((FolderChoice)FolderFilter.SelectedItem).Path;
            var beforeRefresh = backend.Requests.Count;
            backend.ReverseFolders = true; await RefreshStatusAsync(); await LoadPeopleAsync(); await Idle();
            Check("refresh-preserves-identity-without-search", _filters.PersonId == selectedPerson && _filters.Folder == selectedFolder && backend.Requests.Count == beforeRefresh);
            await RemoveChip("person"); Check("person-filter-removed", backend.Requests.Last().Filters.PersonIds.Count == 0);
            await RemoveChip("folder"); Check("folder-filter-removed", backend.Requests.Last().Filters.Folder is null);
            DateFilter.SelectedIndex = 1; await Idle(); Check("date-preset", backend.Requests.Last().Filters.AfterUtc is not null); await RemoveChip("date");
            AfterDate.SelectedDate = new DateTime(2026, 10, 5); BeforeDate.SelectedDate = new DateTime(2026, 10, 12); DateFilter.SelectedIndex = 3; await Idle();
            Check("custom-date-range", backend.Requests.Last().Filters.AfterUtc is not null && backend.Requests.Last().Filters.BeforeUtc > backend.Requests.Last().Filters.AfterUtc && DateRangePanel.Visibility == Visibility.Visible);
            var beforeInvalid = backend.Requests.Count; AfterDate.SelectedDate = new DateTime(2026, 10, 20); await Idle();
            Check("reversed-range-does-not-search", backend.Requests.Count == beforeInvalid && NoticeText.Text.Contains("end date", StringComparison.OrdinalIgnoreCase));
            await RemoveChip("date");

            QueryBox.Text = "sample"; SearchClick(this, new RoutedEventArgs()); await Idle();
            Check("unknown-total-has-more-label", ResultCount.Text.Contains("More results available") && !ResultCount.Text.Contains("of 650"));
            QueryBox.Text = "unsent draft"; await Click(NextPageButton);
            Check("paging-keeps-submitted-query-and-generation", _page == 1 && backend.Requests.Last().Query == "sample" && backend.Requests.Last().GenerationId == 42 && ResultCount.Text.Contains("257"));
            var pageRequests = backend.Requests.Count; await Click(PreviousPageButton);
            Check("previous-page-uses-cache", _page == 0 && backend.Requests.Count == pageRequests && CurrentTiles.Count == 256);
            await Click(NextPageButton); await Click(NextPageButton);
            Check("final-page-and-accurate-range", _page == 2 && CurrentTiles.Count == 138 && !NextPageButton.IsEnabled && ResultCount.Text.Contains("650"));
            QueryBox.Clear(); SearchClick(this, new RoutedEventArgs()); await Idle();

            var densities = new List<object>();
            foreach (var index in new[] { 0, 1, 2 })
            {
                DensitySelector.SelectedIndex = index; await Idle();
                var row = Gallery.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                var actualExtent = row is null ? double.NaN : row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
                Check("density-" + index, Rows.Count == (int)Math.Ceiling(CurrentTiles.Count / (double)_columns) && CurrentTiles.All(t => t.ThumbnailHeight == _galleryMetrics.ThumbnailHeight) && Math.Abs(actualExtent - _galleryMetrics.RowExtent) <= 1,
                    new { _targetTileWidth, columns = _columns, rowCount = Rows.Count, expectedExtent = _galleryMetrics.RowExtent, actualExtent });
                densities.Add(new { index, columns = _columns, _galleryMetrics.ThumbnailHeight });
            }
            Check("density-controls-reflow", densities.Count == 3);
            DensitySelector.SelectedIndex = 1; await Idle();
            var scroll = VisualChildren<ScrollViewer>(Gallery).First();
            scroll.ScrollToVerticalOffset(_galleryMetrics.RowExtent * 4); await Idle();
            var anchor = GalleryLayout.GetAnchorIndex(scroll.VerticalOffset, _columns, _galleryMetrics.RowExtent);
            Width = 1280; await Idle();
            var resizedAnchor = GalleryLayout.GetAnchorIndex(scroll.VerticalOffset, _columns, _galleryMetrics.RowExtent);
            Check("resize-preserves-visible-anchor", resizedAnchor <= anchor && anchor < resizedAnchor + _columns, new { anchor, resizedAnchor, columns = _columns, offset = scroll.VerticalOffset });
            Width = 1400; await Idle();

            ExactFilter.IsChecked = true; await Idle(); var viewFilters = _filters; var viewRequests = backend.Requests.Count;
            await Click(ListViewButton); Check("list-view-keeps-filter-state", _listMode && MetadataList.IsVisible && _filters == viewFilters && backend.Requests.Count == viewRequests);
            await Click(GridViewButton); Check("grid-view-keeps-filter-state", !_listMode && Gallery.IsVisible && _filters == viewFilters && backend.Requests.Count == viewRequests);
            await RemoveChip("exact");
            await Click(ListViewButton);
            var listDocument = CurrentTiles.First(t => t.Result.Kind == FileKind.Document);
            MetadataList.SelectedItem = listDocument; await Idle();
            Check("list-selection-opens-preview", _selectedResult?.FileId == listDocument.Result.FileId && PreviewPanel.IsVisible);
            ClosePreviewClick(this, new RoutedEventArgs()); await Idle();
            Check("closing-list-preview-clears-selection", _selectedResult is null && MetadataList.SelectedItem is null);
            MetadataList.SelectedItem = listDocument; await Idle();
            Check("same-list-row-can-reopen-preview", _selectedResult?.FileId == listDocument.Result.FileId && PreviewPanel.IsVisible);
            ClosePreviewClick(this, new RoutedEventArgs()); await Idle(); await Click(GridViewButton);
            scroll.ScrollToTop(); await Idle();
            Activate();
            var firstTileButton = VisualChildren<Button>(Gallery).First(b => b.DataContext is ResultTile);
            firstTileButton.Focus(); await Idle();
            var focusedTile = (Keyboard.FocusedElement as Button)?.DataContext as ResultTile;
            var nextKey = KeyArgs(Key.Right); WindowKeyDown(this, nextKey); await Idle();
            var nextFocusedTile = (Keyboard.FocusedElement as Button)?.DataContext as ResultTile;
            Check("gallery-arrow-key-navigation", focusedTile is not null && nextKey.Handled && nextFocusedTile is not null && CurrentTiles.IndexOf(nextFocusedTile) == CurrentTiles.IndexOf(focusedTile) + 1);

            var document = CurrentTiles.First(t => t.Result.Kind == FileKind.Document);
            await SelectResultAsync(document.Result); await Idle();
            Check("document-inspector", _preview?.Kind == FileKind.Document && PreviewPanel.IsVisible && _selectedResult?.FileId == document.Result.FileId && PreviewText.Text.Contains("in-memory"));
            LayoutCheck("regular-inspector-layout", QueryBox, Gallery, PreviewPanel);
            Width = 880; Height = 520; await Idle();
            Check("compact-inspector-has-back-and-search", QueryBox.IsVisible && QueryBox.ActualWidth > 200 && LibraryResultsPanel.Visibility == Visibility.Collapsed && PreviewBackButton.IsVisible);
            LayoutCheck("compact-inspector-layout", QueryBox, PreviewPanel, PreviewBackButton);
            // Layout-only regression: use the media shape without creating a player or opening media.
            _preview = _preview! with { Kind = FileKind.Video };
            VideoControls.Visibility = Visibility.Visible; PossibleMatchBanner.Visibility = Visibility.Visible;
            ApplyResponsiveLayout(); await Idle();
            var previewActions = VisualChildren<WrapPanel>(PreviewPanel).Single(panel => panel.Children.OfType<Button>().Any(button => Equals(button.Content, "Open original")));
            var previewScroll = VisualChildren<ScrollViewer>(PreviewPanel).Single(viewer => viewer.IsAncestorOf(VideoControls));
            var panelBounds = Bounds(PreviewPanel); var actionBounds = Bounds(previewActions); var imageBounds = Bounds(PreviewImage);
            Check("compact-video-layout-keeps-actions-visible", previewActions.IsVisible && actionBounds.Left >= panelBounds.Left && actionBounds.Right <= panelBounds.Right && actionBounds.Top >= imageBounds.Bottom && actionBounds.Bottom <= panelBounds.Bottom && previewScroll.ViewportHeight > 0,
                new { panelTop = panelBounds.Top, panelBottom = panelBounds.Bottom, actionTop = actionBounds.Top, actionBottom = actionBounds.Bottom, mediaBottom = imageBounds.Bottom, scrollViewport = previewScroll.ViewportHeight, mediaHeight = PreviewMediaRow.ActualHeight });
            Check("media-controls-and-match-banner-scroll-outside-frame", previewScroll.IsAncestorOf(VideoControls) && previewScroll.IsAncestorOf(PossibleMatchBanner) && VideoPlayer.Visibility == Visibility.Collapsed && _player is null);
            LayoutCheck("compact-video-action-layout", QueryBox, PreviewPanel, PreviewBackButton, previewActions);
            Width = 1366; Height = 768;
            Notice("A sampled face falls within this scene. Check the surrounding footage: action, role and speaker are not verified.");
            PersonEvidenceFaces.ItemsSource = new[] { new EvidenceFaceTile("Sample person", "00:00:06.500 · Confirmed membership", null) };
            PersonEvidenceFaces.Visibility = Visibility.Visible;
            ApplyResponsiveLayout(); await Idle();
            Check("laptop-video-evidence-retains-usable-preview", PreviewMediaRow.ActualHeight >= 96 && previewScroll.ViewportHeight > 0 && Bounds(previewActions).Bottom <= Bounds(PreviewPanel).Bottom,
                new { mediaHeight = PreviewMediaRow.ActualHeight, scrollViewport = previewScroll.ViewportHeight });
            Check("face-evidence-crops-share-scroll-with-controls", previewScroll.IsAncestorOf(PersonEvidenceFaces));
            PersonEvidenceFaces.Visibility = Visibility.Collapsed; PersonEvidenceFaces.ItemsSource = null; HideNotice(true);
            Width = 880; Height = 520; ApplyResponsiveLayout(); await Idle();
            await Click(PreviewBackButton);
            Check("compact-back-restores-gallery", _selectedResult is null && Gallery.IsVisible && LibraryResultsPanel.IsVisible);
            LayoutCheck("compact-library-layout", QueryBox, SearchHeadingRow, AllTab, AudioTab, FiltersButton, Gallery, PaginationBar);
            Check("compact-tabs-and-filters-do-not-overlap", Bounds(AudioTab).Right <= Bounds(FiltersButton).Left);

            await Click(PeopleNav);
            Check("people-gallery-populated", PeoplePage.IsVisible && _personTiles.Count == 40 && PeopleRows.Count > 0);
            var add = VisualChildren<Button>(PeoplePage).First(b => Equals(b.Content, "Add a person")); await Click(add);
            Check("compact-add-person", _personInspectorOpen && _editingPerson is null && PersonName.Text.Length == 0 && PersonBackButton.IsVisible && !PeopleResultsPanel.IsVisible);
            LayoutCheck("compact-person-layout", QueryBox, PersonInspector, PersonBackButton);
            await Click(PersonBackButton);
            var personButton = VisualChildren<Button>(PeopleGallery).First(b => b.Tag is PersonTile); await Click(personButton);
            Check("person-tile-opens-existing-person", _editingPerson is not null && PersonName.Text == _editingPerson.Name && SearchPersonButton.IsEnabled && PersonBackButton.IsVisible);
            var escape = KeyArgs(Key.Escape); WindowKeyDown(this, escape); await Idle();
            Check("escape-closes-person-inspector", escape.Handled && !_personInspectorOpen && PeopleResultsPanel.IsVisible);
            Width = 1400; Height = 900; await Idle();
            await Click(FoldersNav);
            var browse = VisualChildren<Button>(FolderList).First(b => b.Tag is FolderEntry); var folder = (FolderEntry)browse.Tag; await Click(browse);
            Check("folder-browse-opens-filtered-library", SearchPage.IsVisible && _filters.Folder == folder.RelativePath && CurrentTiles.All(t => t.Result.RelativePath.StartsWith(folder.RelativePath + "/", StringComparison.Ordinal)));
            await RemoveChip("folder");

            var cancellationsBeforeSlow = backend.CancelledSearches;
            QueryBox.Text = "slow"; SearchClick(this, new RoutedEventArgs()); await backend.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            QueryBox.Text = "sample"; SearchClick(this, new RoutedEventArgs()); await Idle();
            await backend.SlowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check("stale-search-is-cancelled", backend.CancelledSearches == cancellationsBeforeSlow + 1 && _submittedQuery == "sample" && CurrentTiles.Count == 256 && CurrentTiles.All(t => t.Result.Name.Contains("Sample")),
                new { cancellationsBeforeSlow, cancellationsAfterSlow = backend.CancelledSearches, slowRequestWasCancelled = backend.SlowCancelled.Task.IsCompletedSuccessfully, _submittedQuery, tileCount = CurrentTiles.Count, lastRequestQuery = backend.Requests.Last().Query });
            FiltersPopup.IsOpen = true; await Idle(); escape = KeyArgs(Key.Escape); WindowKeyDown(this, escape); await Idle();
            Check("escape-dismisses-filters", escape.Handled && !FiltersPopup.IsOpen);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _statusTimer.Stop();
            var report = new { passed = failure is null, elapsedMs = watch.Elapsed.TotalMilliseconds, checks, error = failure?.ToString(), archive = "Deterministic in-memory archive; no original files or archive data are read or written.", manualCoverage = new[] { "Ctrl+K uses the physical keyboard modifier state and needs a real keyboard check.", "Narrator, Windows high contrast, physical DPI changes, and actual playback require their separate checks." } };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
            await CleanupAfterCaptureAsync();
        }
        if (failure is not null) throw new InvalidOperationException("The interaction smoke failed. See " + reportPath, failure);
    }

    private sealed record InteractionCheck(string Name, bool Passed, object? Details);

    private sealed class InteractionArchive(string root) : IArchiveBackend
    {
        private static Guid FileId(int index) => Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}");
        private static Guid PersonId(int index) => Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}");
        private static readonly string[] FolderNames = ["Documents", "Photos/Family", "Media/Trips"];
        private readonly SearchResult[] _files = Enumerable.Range(0, 650).Select(index => new SearchResult
        {
            FileId = FileId(index), Name = $"Sample {index + 1:000} — a descriptive file name", RelativePath = FolderNames[index % 3] + $"/Sample-{index + 1:000}.txt",
            Kind = (index % 4) switch { 0 => FileKind.Document, 1 => FileKind.Image, 2 => FileKind.Video, _ => FileKind.Audio }, Bytes = 1024 * (index + 1),
            ModifiedUtc = new DateTimeOffset(2026, 10, index % 20 + 1, 12, 0, 0, TimeSpan.Zero), Snippet = "A deterministic in-memory document excerpt for interaction validation.",
            MatchCategory = MatchCategory.Browse, Moments = [new SearchMoment { SegmentId = index + 1, Kind = SegmentKind.DocumentPage, Page = 1, Label = "Page 1", Text = "An in-memory matching excerpt." }]
        }).ToArray();
        private readonly List<PersonRecord> _records = Enumerable.Range(0, 40).Select(index => new PersonRecord { Id = PersonId(index), Name = $"Person {index + 1:00}", Aliases = [$"Alias {index + 1}"], ObservationCount = index * 7 + 1, Confirmed = true }).ToList();
        public string RootPath => root;
        public List<SearchRequest> Requests { get; } = [];
        public bool ReverseFolders { get; set; }
        public int CancelledSearches { get; private set; }
        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SlowCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ArchiveStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            IEnumerable<FolderEntry> folders = FolderNames.Select(folder => new FolderEntry(folder, _files.Count(f => f.RelativePath.StartsWith(folder + "/", StringComparison.Ordinal))));
            return Task.FromResult(new ArchiveStatus { RootPath = RootPath, DisplayName = "Interaction QA archive", LibraryId = Guid.Parse("20000000-0000-0000-0000-000000000001"), GenerationId = 42, FileCount = _files.Length, SegmentCount = _files.Length, PeopleCount = _records.Count, Connected = true, SemanticAvailable = true, Folders = (ReverseFolders ? folders.Reverse() : folders).ToArray(), Kinds = _files.GroupBy(f => f.Kind).Select(g => new KindCount(g.Key, g.Count())).ToArray() });
        }
        public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            try { if (request.Query == "slow") { SlowStarted.TrySetResult(); await Task.Delay(500, cancellationToken); } else await Task.Delay(10, cancellationToken); }
            catch (OperationCanceledException) { CancelledSearches++; if (request.Query == "slow") SlowCancelled.TrySetResult(); throw; }
            var filters = request.Filters;
            var results = _files.Where((file, index) => (filters.Kind is null || file.Kind == filters.Kind) && (filters.Folder is null || file.RelativePath.StartsWith(filters.Folder + "/", StringComparison.Ordinal)) && (filters.PersonIds.Count == 0 || filters.PersonIds.Contains(PersonId(index % 40))) && (filters.AfterUtc is null || file.ModifiedUtc >= filters.AfterUtc) && (filters.BeforeUtc is null || file.ModifiedUtc < filters.BeforeUtc) && (request.Query.Length == 0 || file.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase))).ToArray();
            var offset = int.TryParse(request.Cursor, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var limit = Math.Min(256, request.Limit); var page = results.Skip(offset).Take(limit).ToArray();
            return new SearchResponse { GenerationId = 42, Results = page, TotalCount = request.Query.Length == 0 ? results.Length : null, NextCursor = offset + page.Length < results.Length ? (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null, ElapsedMilliseconds = 10 };
        }
        public Task<IReadOnlyList<PersonRecord>> GetPeopleAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PersonRecord>>(_records.ToArray());
        public Task<IReadOnlyList<PersonObservation>> GetPersonObservationsAsync(Guid personId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PersonObservation>>([]);
        public Task<PersonRecord> SavePersonAsync(PersonUpdateRequest request, CancellationToken cancellationToken = default)
        {
            var person = new PersonRecord { Id = request.PersonId ?? PersonId(_records.Count), Name = request.Name, Aliases = request.Aliases, Confirmed = true };
            _records.RemoveAll(p => p.Id == person.Id); _records.Add(person); return Task.FromResult(person);
        }
        public Task<PreviewResult> GetPreviewAsync(PreviewRequest request, CancellationToken cancellationToken = default)
        {
            var file = _files.Single(f => f.FileId == request.FileId);
            return Task.FromResult(new PreviewResult { FileId = file.FileId, Kind = FileKind.Document, Text = file.Snippet, Page = 1, Moments = file.Moments });
        }
        public Task<ArchiveStatus> PrepareAsync(PreparationRequest request, IProgress<PreparationProgress>? progress = null, CancellationToken cancellationToken = default) => GetStatusAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
