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
        void CaptureSearchWindow(string name)
        {
            var surface = (FrameworkElement)Content;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            Directory.CreateDirectory(directory);
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        async Task RemoveChip(string key)
        {
            var chip = ActiveFilterPanel.Children.OfType<Button>().Single(b => Equals(b.Tag, key));
            await Click(chip);
            Check("remove-" + key + "-chip", !ActiveFilterPanel.Children.OfType<Button>().Any(b => Equals(b.Tag, key)));
        }
        // Exact words is an inline switch in the filter bar rather than a removable chip.
        async Task ClearExact()
        {
            ExactFilter.IsChecked = false; await Idle();
            Check("exact-switch-off", !_filters.ExactOnly && !backend.Requests.Last().Filters.ExactOnly);
        }
        void AnswerPersonDraft(string answer)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "Unsaved person changes");
                if (dialog is null) return;
                var button = VisualChildren<Button>(dialog).FirstOrDefault(item => Equals(item.Content, answer));
                if (button is null) { dialog.Close(); return; }
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            }, DispatcherPriority.ApplicationIdle);
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
        void CheckFilterPopupBounds(string label)
        {
            var surface = (FrameworkElement)Content;
            var popup = (FrameworkElement)FiltersPopup.Child;
            var surfaceBounds = new Rect(surface.PointToScreen(new Point()), surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight)));
            var popupBounds = new Rect(popup.PointToScreen(new Point()), popup.PointToScreen(new Point(popup.ActualWidth, popup.ActualHeight)));
            Check(label, FiltersPopup.IsOpen && popup.ActualWidth > 0 && popup.ActualHeight > 0 && popupBounds.Left >= surfaceBounds.Left - 2 && popupBounds.Top >= surfaceBounds.Top - 2 && popupBounds.Right <= surfaceBounds.Right + 2 && popupBounds.Bottom <= surfaceBounds.Bottom + 2,
                new { surfaceBounds, popupBounds, FilterScroll.MaxHeight });
        }
        void CheckContactSheetWidth(string label)
        {
            var viewport = VisualChildren<ScrollViewer>(Gallery).First().ViewportWidth;
            var available = Math.Max(120, viewport - GalleryGap);
            var fullRows = _sheetRows.Take(Math.Max(0, _sheetRows.Count - 1)).ToArray();
            var geometryWidthMatches = fullRows.Length > 0 && fullRows.All(row => Math.Abs(row.Widths.Sum() + (row.Count - 1) * GalleryGap - available) <= 1);
            var uniformTiles = fullRows.All(row => row.Widths.All(width => Math.Abs(width - row.Widths[0]) <= 1.5));
            geometryWidthMatches &= uniformTiles;
            Check(label, Gallery.IsVisible && _sheetRows.Count == Rows.Count && Math.Abs(_sheetWidth - available) <= 1 && geometryWidthMatches,
                new { viewport, available, arrangedWidth = _sheetWidth, galleryWidth = Gallery.ActualWidth, rowCount = _sheetRows.Count, geometryWidthMatches });
        }
        try
        {
            Width = 1400; Height = 900;
            await AttachBackendAsync(backend); _statusTimer.Stop(); await Idle();
            Check("small-file-sizes-use-readable-units", FormatBytes(123).Contains("123", StringComparison.Ordinal) && FormatBytes(123).EndsWith(" B", StringComparison.Ordinal) && FormatBytes(1024).EndsWith(" KB", StringComparison.Ordinal) && !FormatBytes(1024).StartsWith("0", StringComparison.Ordinal));
            Check("initial-home-with-loaded-library", HomePage.IsVisible && !TitleSearchHost.IsVisible && SearchNav.IsChecked == true && CurrentTiles.Count == 256 && _people.Count == 40 && _searchGeneration == 42 && NextPageButton.IsEnabled && ResultCount.Text.Contains("650"));
            Check("home-shows-recent-files-and-people", RecentTiles.Count == 12 && HomePeopleList.Items.Count == 8 && HomeLibrarySections.IsVisible && !HomeSetupCard.IsVisible && PhotoCount.Text.Contains("photo"),
                new { recent = RecentTiles.Count, people = HomePeopleList.Items.Count, PhotoCount.Text });
            var requestsBeforeBrowse = backend.Requests.Count;
            await Click(BrowseNav);
            Check("browse-reuses-loaded-window", SearchPage.IsVisible && BrowseNav.IsChecked == true && FolderPane.IsVisible && SearchTitle.Text == "All files" && backend.Requests.Count == requestsBeforeBrowse && CurrentTiles.All(tile => tile.ReasonBadge is null));
            LayoutCheck("regular-library-layout", QueryBox, SearchHeadingRow, AllTab, AudioTab, FiltersButton, Gallery, PaginationBar, FolderPane);
            Check("icon-actions-have-accessible-names", new ButtonBase[] { EjectButton, GridViewButton, ListViewButton, FiltersButton, SearchNav, BrowseNav, PeopleNav, DriveNav, SettingsNav, MinimizeButton, MaximizeButton, CloseButton, DriveStatusButton, PreviewPreviousButton, PreviewNextButton }.All(button => !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button))));
            Check("tabs-and-filters-do-not-overlap", Bounds(AudioTab).Right <= Bounds(FiltersButton).Left);
            QueryBox.Focus(); await Idle(); var focusedQueryHeight = QueryBox.ActualHeight;
            FiltersButton.Focus(); await Idle();
            Check("search-focus-does-not-shift-toolbar-height", Math.Abs(QueryBox.ActualHeight - focusedQueryHeight) <= .5, new { focusedQueryHeight, unfocusedQueryHeight = QueryBox.ActualHeight });

            await Click(PhotosTab);
            Check("photos-tab-search-and-selection", _filters.KindIndex == 1 && PhotosTab.IsChecked == true && AllTab.IsChecked == false && backend.Requests.Last().Filters.Kind == FileKind.Image && CurrentTiles.All(t => t.Result.Kind == FileKind.Image));
            await Click(VideosTab); Check("videos-tab", backend.Requests.Last().Filters.Kind == FileKind.Video && VideosTab.IsChecked == true);
            await Click(DocumentsTab); Check("documents-tab", backend.Requests.Last().Filters.Kind == FileKind.Document && DocumentsTab.IsChecked == true);
            await Click(AudioTab); Check("audio-tab", backend.Requests.Last().Filters.Kind == FileKind.Audio && AudioTab.IsChecked == true);
            await Click(AllTab);
            Activate(); await Click(FiltersButton);
            Check("keyboard-filter-popover-focus", FiltersPopup.IsOpen && DateFilter.IsKeyboardFocusWithin);
            CheckFilterPopupBounds("regular-filter-popover-stays-inside-window");
            await Click(FiltersButton);
            Check("closing-filter-popover-restores-focus", !FiltersPopup.IsOpen && FiltersButton.IsKeyboardFocused);

            ExactFilter.IsChecked = true; await Idle();
            Check("exact-filter", backend.Requests.Last().Filters.ExactOnly && ExactFilter.IsChecked == true); await ClearExact();
            PersonFilter.SelectedIndex = 7; await Idle();
            Check("person-filter-chip-and-count", ActiveFilterPanel.Children.OfType<Button>().Any(b => Equals(b.Tag, "person")) && FilterCountText.Text == "1" && SearchTitle.Text == ((PersonChip)PersonFilter.SelectedItem).Name);
            var selectedPerson = ((PersonChip)PersonFilter.SelectedItem).PersonId;
            FolderFilter.SelectedIndex = 2; await Idle();
            var selectedFolder = ((FolderChoice)FolderFilter.SelectedItem).Path;
            var beforeRefresh = backend.Requests.Count;
            backend.ReverseFolders = true; await RefreshStatusAsync(); await LoadPeopleAsync(); await Idle();
            Check("refresh-preserves-identity-without-search", _filters.PersonId == selectedPerson && _filters.Folder == selectedFolder && backend.Requests.Count == beforeRefresh);
            await RemoveChip("person"); Check("person-filter-removed", backend.Requests.Last().Filters.PersonIds.Count == 0);
            Check("removing-chip-restores-filter-focus", FiltersButton.IsKeyboardFocused);
            await RemoveChip("folder"); Check("folder-filter-removed", backend.Requests.Last().Filters.Folder is null);
            DateFilter.SelectedIndex = 1; await Idle(); Check("date-preset", backend.Requests.Last().Filters.AfterUtc is not null);
            var datePresetCutoff = backend.Requests.Last().Filters.AfterUtc;
            await Click(NextPageButton);
            Check("relative-date-paging-keeps-identical-cutoff", _page == 1 && backend.Requests.Last().Filters.AfterUtc == datePresetCutoff);
            await RemoveChip("date");
            AfterDate.SelectedDate = new DateTime(2026, 10, 5); BeforeDate.SelectedDate = new DateTime(2026, 10, 12); DateFilter.SelectedIndex = 3; await Idle();
            Check("custom-date-range", backend.Requests.Last().Filters.AfterUtc is not null && backend.Requests.Last().Filters.BeforeUtc > backend.Requests.Last().Filters.AfterUtc && DateRangePanel.Visibility == Visibility.Visible);
            var beforeInvalid = backend.Requests.Count; AfterDate.SelectedDate = new DateTime(2026, 10, 20); await Idle();
            Check("reversed-range-does-not-search", backend.Requests.Count == beforeInvalid && NoticeText.Text.Contains("end date", StringComparison.OrdinalIgnoreCase));
            await RemoveChip("date");

            QueryBox.Text = "sample"; SearchClick(this, new RoutedEventArgs()); await Idle();
            Check("unknown-total-has-more-label", ResultCount.Text.Contains("More results available") && !ResultCount.Text.Contains("of 650"));
            Check("search-switches-to-results-mode", SearchTitle.Text.Contains("sample") && SearchNav.IsChecked == true && BrowseNav.IsChecked == false && !FolderPane.IsVisible && SuggestionPanel.Children.OfType<Button>().Any(b => Equals(b.Content, "sample")));
            QueryBox.Text = "unsent draft";
            ExactFilter.IsChecked = true; await Idle();
            Check("filter-keeps-submitted-query", backend.Requests.Last().Query == "sample" && _submittedQuery == "sample" && QueryBox.Text == "unsent draft" && CurrentTiles.Count == 256);
            await ClearExact();
            Check("removing-filter-keeps-submitted-query", backend.Requests.Last().Query == "sample" && QueryBox.Text == "unsent draft");
            var selectedBeforePage = CurrentTiles.First(tile => tile.Result.Kind == FileKind.Document);
            await SelectResultAsync(selectedBeforePage.Result); await Idle();
            backend.FailNextSearch = true; await Click(NextPageButton);
            Check("failed-page-keeps-results-and-navigation", _page == 0 && CurrentTiles.First().Result.FileId == selectedBeforePage.Result.FileId && !PreviousPageButton.IsEnabled && NextPageButton.IsEnabled && _selectedResult?.FileId == selectedBeforePage.Result.FileId);
            await Click(NextPageButton);
            Check("successful-page-change-closes-stale-inspector", _selectedResult is null && !PreviewPanel.IsVisible && CurrentTiles.All(tile => !tile.IsSelected));
            Check("paging-keeps-submitted-query-and-generation", _page == 1 && backend.Requests.Last().Query == "sample" && backend.Requests.Last().GenerationId == 42 && ResultCount.Text.Contains("257"));
            var pageRequests = backend.Requests.Count; await Click(PreviousPageButton);
            Check("previous-page-validates-worker-session", _page == 0 && backend.Requests.Count == pageRequests + 1 && CurrentTiles.Count == 256 && backend.Requests.Last().Cursor == "0");
            await Click(NextPageButton); await Click(NextPageButton);
            Check("final-page-and-accurate-range", _page == 2 && CurrentTiles.Count == 138 && !NextPageButton.IsEnabled && ResultCount.Text.Contains("650"));
            backend.RestartNextSearch = true; await Click(PreviousPageButton);
            Check("expired-session-clears-pages-and-offers-restart", CurrentTiles.Count == 0 && _pages.Count == 0 && _pageCursors.Count == 1 && _searchGeneration is null && !NextPageButton.IsEnabled && !PreviousPageButton.IsEnabled && Equals(EmptyAction.Content, "Try search again"));
            await Click(EmptyAction);
            Check("session-restart-keeps-submitted-query", _page == 0 && backend.Requests.Last().Cursor is null && backend.Requests.Last().Query == "sample" && QueryBox.Text == "unsent draft" && CurrentTiles.Count == 256);
            await Click(SearchMoreButton);
            Check("deeper-search-starts-new-window", _page == 0 && backend.Requests.Last().Cursor is null && backend.Requests.Last().RetrievalBudget == 2048 && CurrentTiles.Count == 256);
            foreach (bool dark in new[] { false, true })
            {
                SetDarkTheme(dark); Width = 880; Height = 520; await Idle();
                Check("bounded-search-controls-fit-compact-" + dark, Bounds(ResultCount).Right <= Bounds(SearchMoreButton).Left && Bounds(NextPageButton).Right <= Bounds(PaginationBar).Right + 1);
                CaptureSearchWindow(dark ? "bounded-search-dark-compact" : "bounded-search-light-compact");
            }
            SetDarkTheme(false); Width = 1400; Height = 900; await Idle();
            QueryBox.Clear(); SearchClick(this, new RoutedEventArgs()); await Idle();

            var densities = new List<object>();
            foreach (var index in new[] { 0, 1, 2 })
            {
                DensitySelector.SelectedIndex = index; await Idle();
                var row = Gallery.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                var actualExtent = row is null ? double.NaN : row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
                var geometryMatchesTiles = _sheetRows.All(geometry => Enumerable.Range(0, geometry.Count).All(item =>
                    Math.Abs(CurrentTiles[geometry.Start + item].TileWidth - geometry.Widths[item]) <= .01 &&
                    Math.Abs(CurrentTiles[geometry.Start + item].ThumbnailHeight - geometry.ThumbnailHeight) <= .01));
                Check("density-" + index, Rows.Count == _sheetRows.Count && _sheetRows.Count > 0 && _sheetRows.Sum(geometry => geometry.Count) == CurrentTiles.Count && geometryMatchesTiles && Math.Abs(actualExtent - _sheetRows[0].Extent) <= 1,
                    new { _targetTileWidth, rowCount = Rows.Count, expectedExtent = _sheetRows.FirstOrDefault()?.Extent, actualExtent, geometryMatchesTiles });
                densities.Add(new { index, rowCount = _sheetRows.Count, firstRowCount = _sheetRows[0].Count, _sheetRows[0].ThumbnailHeight });
            }
            Check("density-controls-reflow", densities.Count == 3);
            DensitySelector.SelectedIndex = 1; await Idle();
            // A resize that keeps the column count resizes realized tiles in place.
            var columnsBefore = _columns; var rowBefore = Gallery.ItemContainerGenerator.ContainerFromIndex(0);
            var buttonBefore = VisualChildren<Button>(Gallery).FirstOrDefault(b => ReferenceEquals(b.DataContext, CurrentTiles[0]));
            Width = 1380; await Idle();
            var rowAfter = Gallery.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var buttonAfter = VisualChildren<Button>(Gallery).FirstOrDefault(b => ReferenceEquals(b.DataContext, CurrentTiles[0]));
            Check("resize-keeps-columns-in-place", _columns == columnsBefore && rowAfter is not null && ReferenceEquals(rowBefore, rowAfter) && buttonAfter is not null && ReferenceEquals(buttonBefore, buttonAfter)
                && Math.Abs(buttonAfter.ActualWidth - _sheetRows[0].Widths[0]) <= 1 && Math.Abs(rowAfter.ActualHeight - _sheetRows[0].Extent) <= 1,
                new { columnsBefore, _columns, sameRow = ReferenceEquals(rowBefore, rowAfter), sameButton = ReferenceEquals(buttonBefore, buttonAfter), buttonWidth = buttonAfter?.ActualWidth, expectedWidth = _sheetRows[0].Widths[0], rowHeight = rowAfter?.ActualHeight, expectedExtent = _sheetRows[0].Extent });
            Width = 1400; await Idle();
            var scroll = VisualChildren<ScrollViewer>(Gallery).First();
            scroll.ScrollToVerticalOffset(_sheetRows[4].Offset); await Idle();
            var anchor = GetGalleryAnchor();
            Width = 1280; await Idle();
            var resizedAnchor = GetGalleryAnchor();
            var containingRow = _sheetRows[ContactSheetLayout.GetRowIndex(_sheetRows, anchor)];
            Check("resize-preserves-visible-anchor", resizedAnchor == containingRow.Start && resizedAnchor <= anchor && anchor < resizedAnchor + containingRow.Count,
                new { anchor, resizedAnchor, containingRow.Count, expectedOffset = containingRow.Offset, offset = scroll.VerticalOffset });
            Width = 1400; await Idle();

            await Click(ListViewButton); backend.ReverseResults = true;
            QueryBox.Text = "sample"; SearchClick(this, new RoutedEventArgs()); await Idle();
            var listSearchFirstFile = CurrentTiles.First().Result.FileId;
            await Click(GridViewButton);
            Check("list-search-rebinds-gallery-at-identical-result-count", Rows.SelectMany(row => row.Tiles).SequenceEqual(CurrentTiles) && Rows.First().Tiles.First().Result.FileId == listSearchFirstFile);
            backend.ReverseResults = false; QueryBox.Clear(); SearchClick(this, new RoutedEventArgs()); await Idle();

            ExactFilter.IsChecked = true; await Idle(); var viewFilters = _filters; var viewRequests = backend.Requests.Count;
            await Click(ListViewButton); Check("list-view-keeps-filter-state", _listMode && MetadataList.IsVisible && _filters == viewFilters && backend.Requests.Count == viewRequests);
            await Click(GridViewButton); Check("grid-view-keeps-filter-state", !_listMode && Gallery.IsVisible && _filters == viewFilters && backend.Requests.Count == viewRequests);
            await ClearExact();
            await Click(ListViewButton);
            var firstListRow = MetadataList.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
            var firstListName = firstListRow is null ? null : System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(firstListRow)?.GetName();
            Check("list-rows-announce-file-names", firstListName?.Contains(CurrentTiles[0].Result.Name, StringComparison.Ordinal) == true, new { firstListName });
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

            var expectedDown = ContactSheetLayout.FindVerticalNeighbor(_sheetRows, CurrentTiles.IndexOf(nextFocusedTile!), 1);
            WindowKeyDown(this, KeyArgs(Key.Down)); await Idle();
            Check("gallery-down-follows-visible-column", ReferenceEquals((Keyboard.FocusedElement as Button)?.DataContext, CurrentTiles[expectedDown]));
            WindowKeyDown(this, KeyArgs(Key.End)); await Idle();
            Check("gallery-end-reaches-virtualized-last-file", ReferenceEquals((Keyboard.FocusedElement as Button)?.DataContext, CurrentTiles.Last()));
            WindowKeyDown(this, KeyArgs(Key.Home)); await Idle();
            Check("gallery-home-returns-to-first-file", ReferenceEquals((Keyboard.FocusedElement as Button)?.DataContext, CurrentTiles.First()));
            nextFocusedTile = (Keyboard.FocusedElement as Button)?.DataContext as ResultTile;
            ArrangeRows(true); await Idle();
            Check("gallery-reflow-keeps-keyboard-focus", ReferenceEquals((Keyboard.FocusedElement as Button)?.DataContext, nextFocusedTile));
            QueryBox.Focus(); ArrangeRows(true); await Idle();
            Check("gallery-reflow-does-not-steal-search-focus", QueryBox.IsKeyboardFocusWithin);

            var document = CurrentTiles.First(t => t.Result.Kind == FileKind.Document);
            await SelectResultAsync(document.Result); await Idle();
            Check("document-inspector", _preview?.Kind == FileKind.Document && PreviewPanel.IsVisible && _selectedResult?.FileId == document.Result.FileId && _previewPlainText.Contains("in-memory") && PreviewTextCard.IsVisible);
            var documentIndex = CurrentTiles.IndexOf(document);
            await Click(PreviewNextButton);
            Check("preview-next-steps-through-results", _selectedResult?.FileId == CurrentTiles[documentIndex + 1].Result.FileId && PreviewPreviousButton.IsEnabled);
            await SelectResultAsync(document.Result); await Idle();
            LayoutCheck("regular-inspector-layout", QueryBox, Gallery, PreviewPanel);
            CheckContactSheetWidth("opening-docked-inspector-reflows-gallery");
            var dockedGalleryWidth = Gallery.ActualWidth;
            PreviewResizeStarted(PreviewSplitter, new DragStartedEventArgs(0, 0));
            PreviewColumn.Width = new GridLength(420); await Idle();
            PreviewResized(PreviewSplitter, new DragCompletedEventArgs(80, 0, false)); await Idle();
            Check("resizing-docked-inspector-changes-gallery-width", Math.Abs(PreviewColumn.ActualWidth - 420) <= 1 && Gallery.ActualWidth < dockedGalleryWidth - 20,
                new { dockedGalleryWidth, resizedGalleryWidth = Gallery.ActualWidth, inspectorWidth = PreviewColumn.ActualWidth });
            CheckContactSheetWidth("resizing-docked-inspector-reflows-gallery");
            ClosePreviewClick(this, new RoutedEventArgs()); await Idle();
            Check("closing-docked-inspector-restores-library", _selectedResult is null && !PreviewPanel.IsVisible && LibraryResultsPanel.IsVisible);
            CheckContactSheetWidth("closing-docked-inspector-refills-gallery");
            var restoredTile = (Keyboard.FocusedElement as Button)?.DataContext as ResultTile;
            Check("closing-docked-inspector-restores-selected-file-focus", restoredTile?.Result.FileId == document.Result.FileId,
                new { expectedFile = document.Result.FileId, focusedFile = restoredTile?.Result.FileId });
            await SelectResultAsync(document.Result); await Idle();
            Width = 880; Height = 520; await Idle();
            Check("compact-inspector-is-sheet-over-results", QueryBox.IsVisible && QueryBox.ActualWidth > 200 && LibraryResultsPanel.IsVisible && PreviewScrim.IsVisible && PreviewPanel.ActualWidth <= 441 && Bounds(PreviewPanel).Right >= Bounds(ResultsArea).Right - 1,
                new { panelWidth = PreviewPanel.ActualWidth, panelRight = Bounds(PreviewPanel).Right, areaRight = Bounds(ResultsArea).Right });
            LayoutCheck("compact-inspector-layout", QueryBox, PreviewPanel, PreviewScrim);
            // Layout-only regression: use the media shape without creating a player or opening media.
            _preview = _preview! with { Kind = FileKind.Video };
            VideoControls.Visibility = Visibility.Visible; PossibleMatchBanner.Visibility = Visibility.Visible;
            ApplyResponsiveLayout(); await Idle();
            var previewActions = VisualChildren<WrapPanel>(PreviewPanel).Single(panel => panel.Children.Contains(OpenOriginalButton));
            var previewScroll = VisualChildren<ScrollViewer>(PreviewPanel).Single(viewer => viewer.IsAncestorOf(VideoControls));
            var panelBounds = Bounds(PreviewPanel); var actionBounds = Bounds(previewActions); var imageBounds = Bounds(PreviewImage);
            Check("compact-video-layout-keeps-actions-visible", previewActions.IsVisible && actionBounds.Left >= panelBounds.Left && actionBounds.Right <= panelBounds.Right && actionBounds.Top >= imageBounds.Bottom && actionBounds.Bottom <= panelBounds.Bottom && previewScroll.ViewportHeight > 0,
                new { panelTop = panelBounds.Top, panelBottom = panelBounds.Bottom, actionTop = actionBounds.Top, actionBottom = actionBounds.Bottom, mediaBottom = imageBounds.Bottom, scrollViewport = previewScroll.ViewportHeight, mediaHeight = PreviewMediaRow.ActualHeight });
            Check("media-controls-and-match-banner-scroll-outside-frame", previewScroll.IsAncestorOf(VideoControls) && previewScroll.IsAncestorOf(PossibleMatchBanner) && VideoPlayer.Visibility == Visibility.Collapsed && _player is null);
            LayoutCheck("compact-video-action-layout", QueryBox, PreviewPanel, previewActions);
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
            PreviewScrimClick(PreviewScrim, null!); await Idle();
            Check("compact-scrim-closes-sheet", _selectedResult is null && Gallery.IsVisible && LibraryResultsPanel.IsVisible && !PreviewScrim.IsVisible && !PreviewPanel.IsVisible);
            LayoutCheck("compact-library-layout", QueryBox, SearchHeadingRow, AllTab, AudioTab, FiltersButton, Gallery, PaginationBar);
            Check("compact-tabs-and-filters-do-not-overlap", Bounds(AudioTab).Right <= Bounds(FiltersButton).Left);
            await Click(FiltersButton);
            CheckFilterPopupBounds("compact-filter-popover-stays-inside-window");
            await Click(FiltersButton);

            await Click(PeopleNav);
            Check("people-gallery-populated", PeoplePage.IsVisible && _personTiles.Count == 40 && PeopleRows.Count > 0);
            var add = VisualChildren<Button>(PeoplePage).First(b => Equals(b.Content, "Add a person")); await Click(add);
            Check("compact-add-person", _personInspectorOpen && _editingPerson is null && PersonName.Text.Length == 0 && PersonBackButton.IsVisible && !PeopleResultsPanel.IsVisible);
            LayoutCheck("compact-person-layout", QueryBox, PersonInspector, PersonBackButton);
            await Click(PersonBackButton);
            var personButton = VisualChildren<Button>(PeopleGallery).First(b => b.Tag is PersonTile); await Click(personButton);
            Check("person-tile-opens-existing-person", _editingPerson is not null && PersonName.Text == _editingPerson.Name && SearchPersonButton.IsEnabled && PersonBackButton.IsVisible);
            var editingId = _editingPerson!.Id;
            PersonName.Text = "Unsaved person name"; PersonAliases.Text = "Draft alias"; _references = ["not-read-reference.jpg"];
            await LoadPeopleAsync(); await Idle();
            Check("people-refresh-preserves-editor-draft", _editingPerson?.Id == editingId && PersonName.Text == "Unsaved person name" && PersonAliases.Text == "Draft alias" && _references.SequenceEqual(new[] { "not-read-reference.jpg" }));
            AnswerPersonDraft("_Keep editing"); await Click(PersonBackButton);
            Check("keep-editing-preserves-unsaved-person", _personInspectorOpen && PersonName.Text == "Unsaved person name" && _references.Length == 1);
            var alternativePerson = _people.First(person => person.Id != editingId);
            AnswerPersonDraft("_Keep editing"); PeopleList.SelectedItem = alternativePerson; await Idle();
            Check("cancelled-person-switch-restores-selection", _editingPerson?.Id == editingId && (PeopleList.SelectedItem as PersonRecord)?.Id == editingId && PersonName.Text == "Unsaved person name");
            AnswerPersonDraft("_Discard changes"); var escape = KeyArgs(Key.Escape); WindowKeyDown(this, escape); await Idle();
            Check("escape-closes-person-inspector", escape.Handled && !_personInspectorOpen && PeopleResultsPanel.IsVisible);
            Check("discard-person-restores-saved-values", PersonName.Text == _editingPerson!.Name && _references.Length == 0);
            var restoredPersonFocus = Keyboard.FocusedElement as FrameworkElement;
            var focusedPersonId = (restoredPersonFocus?.DataContext as PersonTile)?.Person.Id;
            Check("person-back-restores-keyboard-focus", focusedPersonId == editingId,
                new { expectedPersonId = editingId, focusedPersonId, focusedElement = restoredPersonFocus?.GetType().Name, focusedName = restoredPersonFocus?.Name, _peopleRowsDirty, visibleModelsAreCurrent = PeopleRows.SelectMany(row => row.Tiles).All(tile => _personTiles.Contains(tile)) });
            Check("hidden-people-refresh-rebinds-visible-rows", PeopleRows.SelectMany(row => row.Tiles).All(tile => _personTiles.Contains(tile)));
            PeopleList.SelectedItem = null; PeopleList.SelectedItem = _people.First(person => person.Id == editingId); await Idle();
            PersonName.Text = "Saved person name"; backend.DelayNextSave = true;
            var savePerson = VisualChildren<Button>(PersonInspector).First(button => Equals(button.Content, "Save person"));
            savePerson.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, savePerson));
            await backend.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            PeopleList.SelectedItem = alternativePerson; ClosePersonClick(this, new RoutedEventArgs()); await Idle();
            Check("pending-person-save-blocks-switch-and-close", _savingPerson && _personInspectorOpen && _editingPerson?.Id == editingId && (PeopleList.SelectedItem as PersonRecord)?.Id == editingId && !PersonInspector.IsEnabled && !PeopleGallery.IsEnabled);
            backend.ReleaseSave.TrySetResult();
            var saveDeadline = Stopwatch.StartNew();
            while (_savingPerson && saveDeadline.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(20);
            await Idle();
            Check("person-save-completes-with-edited-record", !_savingPerson && PersonInspector.IsEnabled && PeopleGallery.IsEnabled && _editingPerson?.Id == editingId && PersonName.Text == "Saved person name" && !HasUnsavedPersonChanges);
            await Click(PersonBackButton);
            Width = 1400; Height = 900; await Idle();
            await Click(SettingsNav);
            Check("settings-page", SettingsPage.IsVisible && SettingsNav.IsChecked == true && ThemeLightOption.IsChecked == true && !string.IsNullOrWhiteSpace(VersionText.Text));
            ThemeDarkOption.IsChecked = true; await Idle();
            var darkLayer = ((System.Windows.Media.SolidColorBrush)FindResource("LayerBrush")).Color;
            ThemeLightOption.IsChecked = true; await Idle();
            Check("settings-theme-cards-switch-palette", darkLayer.R < 80 && ((System.Windows.Media.SolidColorBrush)FindResource("LayerBrush")).Color.R > 200 && !_dark);
            await Click(DriveNav);
            Check("drive-page", DrivePage.IsVisible && DriveNav.IsChecked == true && DriveTitle.Text == "Interaction QA archive" && DriveStatusText.Text.StartsWith("Ready to search", StringComparison.Ordinal) && PreparedFoldersCard.IsVisible && !WelcomeCard.IsVisible && Equals(StartPreparationButton.Content, "Update index"));
            await Click(BrowseNav);
            var browse = VisualChildren<Button>(FolderList).First(b => b.Tag is FolderEntry); var folder = (FolderEntry)browse.Tag; await Click(browse);
            Check("folder-browse-opens-filtered-library", SearchPage.IsVisible && _filters.Folder == folder.RelativePath && CurrentTiles.All(t => t.Result.RelativePath.StartsWith(folder.RelativePath + "/", StringComparison.Ordinal)) && SearchTitle.Text.Contains(folder.RelativePath.Split('/')[0]));
            await RemoveChip("folder");

            ExactFilter.IsChecked = true; await Idle(); var retainedFailureFilters = _filters;
            QueryBox.Text = "Sample 00"; backend.FailNextSearch = true; SearchClick(this, new RoutedEventArgs()); await Idle();
            Check("failed-reset-clears-stale-results-and-offers-retry", CurrentTiles.Count == 0 && EmptyState.IsVisible && Equals(EmptyAction.Content, "Try search again") && _filters == retainedFailureFilters && _submittedQuery == "Sample 00");
            QueryBox.Text = "unsent retry draft"; await Click(EmptyAction);
            Check("search-retry-keeps-submitted-query-and-filters", CurrentTiles.Count > 0 && backend.Requests.Last().Query == "Sample 00" && QueryBox.Text == "unsent retry draft" && _filters == retainedFailureFilters);
            await ClearExact();
            QueryBox.Text = "zzzz no such file"; ExactFilter.IsChecked = true; await Idle(); SearchClick(this, new RoutedEventArgs()); await Idle();
            Check("no-results-offers-concrete-fixes", CurrentTiles.Count == 0 && EmptyState.IsVisible && EmptyTitle.Text.Contains("zzzz") && EmptySuggestionsCard.IsVisible && VisualChildren<Button>(EmptySuggestions).Any(b => Equals(b.Content, "Turn off Exact words")));
            await Click(VisualChildren<Button>(EmptySuggestions).First(b => Equals(b.Content, "Turn off Exact words")));
            Check("no-results-fix-applies-filter", !_filters.ExactOnly && !backend.Requests.Last().Filters.ExactOnly && backend.Requests.Last().Query == "zzzz no such file");

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
        public bool ReverseResults { get; set; }
        public bool FailNextSearch { get; set; }
        public bool RestartNextSearch { get; set; }
        public bool DelayNextSave { get; set; }
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (FailNextSearch) { FailNextSearch = false; throw new IOException("Simulated search failure."); }
            if (RestartNextSearch) { RestartNextSearch = false; return new SearchResponse { GenerationId = 42, RestartRequired = true, Notice = "Session expired. Run the search again." }; }
            var filters = request.Filters;
            var results = _files.Where((file, index) => (filters.Kind is null || file.Kind == filters.Kind) && (filters.Folder is null || file.RelativePath.StartsWith(filters.Folder + "/", StringComparison.Ordinal)) && (filters.PersonIds.Count == 0 || filters.PersonIds.Contains(PersonId(index % 40))) && (filters.AfterUtc is null || file.ModifiedUtc >= filters.AfterUtc) && (filters.BeforeUtc is null || file.ModifiedUtc < filters.BeforeUtc) && (request.Query.Length == 0 || file.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (ReverseResults) Array.Reverse(results);
            var offset = int.TryParse(request.Cursor, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var limit = Math.Min(256, request.Limit); var page = results.Skip(offset).Take(limit).ToArray();
            return new SearchResponse { GenerationId = 42, Results = page, TotalCount = request.Query.Length == 0 ? results.Length : null,
                PageCursor = offset.ToString(CultureInfo.InvariantCulture), IsBoundedWindow = request.Query.Length > 0,
                CanSearchDeeper = request.Query.Length > 0, RetrievalBudget = request.RetrievalBudget ?? 1024,
                NextCursor = offset + page.Length < results.Length ? (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null, ElapsedMilliseconds = 10 };
        }
        public Task<IReadOnlyList<PersonRecord>> GetPeopleAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PersonRecord>>(_records.ToArray());
        public Task<IReadOnlyList<PersonObservation>> GetPersonObservationsAsync(Guid personId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PersonObservation>>([]);
        public async Task<PersonRecord> SavePersonAsync(PersonUpdateRequest request, CancellationToken cancellationToken = default)
        {
            if (DelayNextSave) { DelayNextSave = false; SaveStarted.TrySetResult(); await ReleaseSave.Task.WaitAsync(cancellationToken); }
            var person = new PersonRecord { Id = request.PersonId ?? PersonId(_records.Count), Name = request.Name, Aliases = request.Aliases, Confirmed = true };
            _records.RemoveAll(p => p.Id == person.Id); _records.Add(person); return person;
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
