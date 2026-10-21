using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FindIt.Shared;

namespace FindIt.App;

public partial class MainWindow
{
    private LibraryFilterState _filters = new();
    private string _submittedQuery = "";
    private IReadOnlyList<ContactSheetRow> _sheetRows = [];
    private double _sheetWidth;
    private int _sheetVersion;
    private bool _aspectReflowQueued;
    private double _targetTileWidth = 220, _inspectorWidth = 340;
    private bool _uiReady, _arranging, _listMode, _compactPreview, _personInspectorOpen, _playbackSmoke;
    private int _returnAnchor, _peopleColumns;
    private bool _peopleRowsDirty;
    private Guid? _lastResultId;
    private readonly List<PersonTile> _personTiles = [];
    private readonly Dictionary<PersonTile, CancellationTokenSource> _portraitRequests = [];
    public ObservableCollection<PersonRow> PeopleRows { get; } = [];
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _pollingPlayback, _seeking, _resizingInspector;

    private void CaptureFilterState()
    {
        _filters = new(Math.Max(0, KindFilter.SelectedIndex), Math.Max(0, DateFilter.SelectedIndex),
            (FolderFilter.SelectedItem as FolderChoice)?.Path,
            (PersonFilter.SelectedItem as PersonChip)?.PersonId,
            ExactFilter.IsChecked == true, AfterDate.SelectedDate, BeforeDate.SelectedDate);
    }

    private void UpdateFilterUi()
    {
        if (!_uiReady) return;
        var tabs = new[] { AllTab, PhotosTab, VideosTab, DocumentsTab, AudioTab };
        for (var i = 0; i < tabs.Length; i++) tabs[i].IsChecked = i == _filters.KindIndex;
        DateRangePanel.Visibility = _filters.DateIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        ActiveFilterPanel.Children.Clear();
        void Chip(string label, string key)
        {
            var button = new Button { Style = (Style)FindResource("FilterChip"), Tag = key, ToolTip = label, Content = new TextBlock { Text = label + "  ×", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 } };
            AutomationProperties.SetName(button, $"Remove filter: {label}");
            button.Click += RemoveFilterClick;
            ActiveFilterPanel.Children.Add(button);
        }
        if (_filters.DateIndex > 0)
            Chip(_filters.DateIndex switch { 1 => "Last 30 days", 2 => "This year", _ => $"{_filters.After?.ToString("dd MMM yyyy") ?? "Any date"} to {_filters.Before?.ToString("dd MMM yyyy") ?? "Any date"}" }, "date");
        if (_filters.Folder is { } folder) Chip("Folder: " + folder, "folder");
        if (_filters.PersonId is Guid person && person != Guid.Empty) Chip("Person: " + (PersonFilter.SelectedItem as PersonChip)?.Name, "person");
        if (_filters.ExactOnly) Chip("Exact words", "exact");
        if (ActiveFilterPanel.Children.Count > 0)
        {
            var clear = new Button { Content = "Clear filters", Style = (Style)FindResource("QuietButton"), MinHeight = 28, Padding = new Thickness(8, 3, 8, 3) };
            clear.Click += ClearFiltersClick; ActiveFilterPanel.Children.Add(clear);
            ActiveFilterPanel.Visibility = Visibility.Visible;
        }
        else ActiveFilterPanel.Visibility = Visibility.Collapsed;
        FiltersButton.SetResourceReference(BackgroundProperty, ActiveFilterPanel.Children.Count > 0 ? "TintBrush" : "SurfaceBrush");
    }

    private void FiltersClick(object sender, RoutedEventArgs e)
    {
        if (!FiltersPopup.IsOpen)
        {
            var surface = (FrameworkElement)Content;
            var bottom = FiltersButton.TranslatePoint(new Point(0, FiltersButton.ActualHeight), surface).Y;
            FilterScroll.MaxHeight = Math.Clamp(surface.ActualHeight - bottom - 64, 120, 380);
        }
        FiltersPopup.IsOpen = !FiltersPopup.IsOpen;
        if (FiltersPopup.IsOpen)
            Dispatcher.BeginInvoke(() => { if (FiltersPopup.IsOpen) DateFilter.Focus(); }, DispatcherPriority.Input);
        else FiltersButton.Focus();
    }
    private void TypeTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && int.TryParse(tag, out var index))
        {
            if (KindFilter.SelectedIndex == index) UpdateFilterUi();
            else KindFilter.SelectedIndex = index;
        }
    }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || !_initialized) return;
        CaptureFilterState(); UpdateFilterUi(); await SearchAsync(true);
    }
    private async void DateRangeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || !_initialized || DateFilter.SelectedIndex != 3) return;
        CaptureFilterState(); UpdateFilterUi(); await SearchAsync(true);
    }
    private async void ExactChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || !_initialized) return;
        CaptureFilterState(); UpdateFilterUi(); await SearchAsync(true);
    }
    private async void RemoveFilterClick(object sender, RoutedEventArgs e)
    {
        var initialized = _initialized; _initialized = false;
        switch ((sender as Button)?.Tag as string)
        {
            case "date": DateFilter.SelectedIndex = 0; AfterDate.SelectedDate = BeforeDate.SelectedDate = null; break;
            case "folder": FolderFilter.SelectedIndex = 0; break;
            case "person": PersonFilter.SelectedIndex = 0; break;
            case "exact": ExactFilter.IsChecked = false; break;
        }
        _initialized = initialized; CaptureFilterState(); UpdateFilterUi(); FiltersButton.Focus(); await SearchAsync(true);
    }
    private void ResetFilterControls()
    {
        var initialized = _initialized; _initialized = false;
        KindFilter.SelectedIndex = DateFilter.SelectedIndex = FolderFilter.SelectedIndex = PersonFilter.SelectedIndex = 0;
        AfterDate.SelectedDate = BeforeDate.SelectedDate = null; ExactFilter.IsChecked = false;
        _initialized = initialized; CaptureFilterState(); UpdateFilterUi();
    }
    private async void ClearFiltersClick(object sender, RoutedEventArgs e)
    {
        var removedControl = sender is DependencyObject control && ActiveFilterPanel.IsAncestorOf(control);
        ResetFilterControls();
        if (removedControl) FiltersButton.Focus();
        await SearchAsync(true);
    }
    private async void SearchClick(object sender, RoutedEventArgs e) { Navigate("search"); await SearchAsync(true, submitQuery: true); }
    private async void QueryKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Navigate("search"); await SearchAsync(true, submitQuery: true); } }
    private async void PreviousClick(object sender, RoutedEventArgs e) { if (_page > 0) await SearchAsync(false, _page - 1); }
    private async void NextClick(object sender, RoutedEventArgs e) { if (_pageCursors.Count > _page + 1) await SearchAsync(false, _page + 1); }

    private void DensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _targetTileWidth = DensitySelector.SelectedIndex switch { 0 => 176, 2 => 264, _ => 220 };
        ArrangeRows(true);
    }
    private void ArrangeRows(bool force = false)
    {
        if (!_uiReady || _arranging || LibraryResultsPanel.Visibility != Visibility.Visible || Gallery.ActualWidth < 1) return;
        _arranging = true;
        try
        {
            var scroll = VisualChildren<ScrollViewer>(Gallery).FirstOrDefault();
            var available = Math.Max(120, (scroll is { ViewportWidth: > 0 } ? scroll.ViewportWidth : Gallery.ActualWidth - SystemParameters.VerticalScrollBarWidth) - 8);
            if (!force && Math.Abs(available - _sheetWidth) < .5 && _sheetRows.Sum(row => row.Count) == CurrentTiles.Count && Rows.SelectMany(row => row.Tiles).SequenceEqual(CurrentTiles)) return;
            var anchor = GetGalleryAnchor();
            var focusedButton = Keyboard.FocusedElement as Button;
            var focusedTile = focusedButton?.DataContext as ResultTile;
            if (focusedTile is not null && !CurrentTiles.Contains(focusedTile)) focusedTile = null;
            _sheetWidth = available;
            _sheetRows = ContactSheetLayout.Arrange(CurrentTiles.Select(tile => tile.AspectRatio).ToArray(), available, _targetTileWidth * .75);
            _columns = _sheetRows.FirstOrDefault()?.Count ?? 1;
            _sheetVersion++;
            Rows.Clear();
            foreach (var row in _sheetRows)
            {
                var tiles = CurrentTiles.Skip(row.Start).Take(row.Count).ToArray();
                for (var i = 0; i < tiles.Length; i++) { tiles[i].TileWidth = row.Widths[i]; tiles[i].ThumbnailHeight = row.ThumbnailHeight; }
                Rows.Add(new(tiles, row.Extent));
            }
            if (scroll is not null && anchor > 0)
                ScrollGalleryToAnchor(anchor);
            if (focusedTile is not null) RestoreGalleryReflowFocus(focusedTile, focusedButton!, _sheetVersion);
        }
        finally { _arranging = false; }
    }
    // WPF rounds and estimates virtualized row extents. Use a realized row for the
    // visible anchor, then correct against its measured position after a reflow.
    private int GetGalleryAnchor()
    {
        var viewport = VisualChildren<ScrollContentPresenter>(Gallery).FirstOrDefault();
        if (viewport is not null)
            for (var index = 0; index < Rows.Count; index++)
                if (Gallery.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
                {
                    var top = container.TranslatePoint(new Point(), viewport).Y;
                    if (top + container.ActualHeight > 1 && top < viewport.ActualHeight && index < _sheetRows.Count) return _sheetRows[index].Start;
                }
        return ContactSheetLayout.FindAnchorIndex(_sheetRows, VisualChildren<ScrollViewer>(Gallery).FirstOrDefault()?.VerticalOffset ?? 0);
    }
    private void ScrollGalleryToAnchor(int anchor)
    {
        if (_sheetRows.Count == 0) return;
        var version = _sheetVersion;
        var rowIndex = ContactSheetLayout.GetRowIndex(_sheetRows, anchor);
        var row = Rows.ElementAtOrDefault(rowIndex);
        var scroll = VisualChildren<ScrollViewer>(Gallery).FirstOrDefault();
        if (row is null || scroll is null) return;
        scroll.ScrollToVerticalOffset(ContactSheetLayout.GetOffsetForIndex(_sheetRows, anchor));
        Dispatcher.BeginInvoke(() =>
        {
            if (version != _sheetVersion || !Gallery.IsVisible) return;
            Gallery.ScrollIntoView(row);
            Gallery.UpdateLayout();
            if (version != _sheetVersion) return;
            if (Gallery.ItemContainerGenerator.ContainerFromIndex(rowIndex) is not ListBoxItem container || VisualChildren<ScrollContentPresenter>(Gallery).FirstOrDefault() is not { } viewport) return;
            var top = container.TranslatePoint(new Point(), viewport).Y;
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + top));
        }, DispatcherPriority.Loaded);
    }
    private void RestoreGalleryReflowFocus(ResultTile tile, Button previousButton, int version)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (version != _sheetVersion || !Gallery.IsVisible) return;
            // A reflow removes the focused button. Restore that file, but never take
            // focus back from a control the user selected while layout was settling.
            if (Keyboard.FocusedElement is Visual current && !ReferenceEquals(current, previousButton) &&
                !ReferenceEquals(current, Gallery) && !ReferenceEquals(current, this) && PresentationSource.FromVisual(current) is not null) return;
            VisualChildren<Button>(Gallery).FirstOrDefault(button => ReferenceEquals(button.DataContext, tile))?.Focus();
        }, DispatcherPriority.Loaded);
    }
    private void QueueThumbnailReflow()
    {
        if (_aspectReflowQueued) return;
        _aspectReflowQueued = true;
        Dispatcher.BeginInvoke(() => { _aspectReflowQueued = false; if (!_closing) ArrangeRows(true); }, DispatcherPriority.ContextIdle);
    }
    private void GalleryScrollToTop()
    {
        Dispatcher.BeginInvoke(() => { VisualChildren<ScrollViewer>(Gallery).FirstOrDefault()?.ScrollToTop(); VisualChildren<ScrollViewer>(MetadataList).FirstOrDefault()?.ScrollToTop(); }, DispatcherPriority.Loaded);
    }
    private void GridViewClick(object sender, RoutedEventArgs e) { _listMode = false; Gallery.Visibility = Visibility.Visible; MetadataList.Visibility = Visibility.Collapsed; DensitySelector.IsEnabled = true; UpdateViewButtons(); ArrangeRows(true); }
    private void ListViewClick(object sender, RoutedEventArgs e) { _listMode = true; Gallery.Visibility = Visibility.Collapsed; MetadataList.Visibility = Visibility.Visible; DensitySelector.IsEnabled = false; UpdateViewButtons(); if (_selectedResult is not null) MetadataList.SelectedItem = CurrentTiles.FirstOrDefault(t => t.Result.FileId == _selectedResult.FileId); }
    private void UpdateViewButtons()
    {
        GridViewButton.SetResourceReference(BackgroundProperty, _listMode ? "SurfaceBrush" : "TintBrush");
        ListViewButton.SetResourceReference(BackgroundProperty, _listMode ? "TintBrush" : "SurfaceBrush");
    }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e) { ApplyResponsiveLayout(); ArrangeRows(); ArrangePeopleRows(); }
    private void GallerySizeChanged(object sender, SizeChangedEventArgs e) => Dispatcher.BeginInvoke(() => ArrangeRows(), DispatcherPriority.Loaded);
    private void ResultsSizeChanged(object sender, SizeChangedEventArgs e) { ApplyResponsiveLayout(); ArrangeRows(); }
    private void ApplyResponsiveLayout()
    {
        if (!_uiReady) return;
        var hasPreview = _selectedResult is not null || _playbackSmoke;
        var compact = ActualWidth < 1200;
        var detailOnly = compact && hasPreview;
        if (detailOnly && !_compactPreview)
            _returnAnchor = GetGalleryAnchor();
        var restore = _compactPreview && !detailOnly; _compactPreview = detailOnly;
        LibraryResultsPanel.Visibility = detailOnly ? Visibility.Collapsed : Visibility.Visible;
        GalleryColumn.Width = detailOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PreviewPanel.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.MinWidth = hasPreview && !compact ? 300 : 0;
        PreviewColumn.MaxWidth = hasPreview && !compact ? 480 : double.PositiveInfinity;
        if (!_resizingInspector || compact) PreviewColumn.Width = !hasPreview ? new GridLength(0) : compact ? new GridLength(1, GridUnitType.Star) : new GridLength(_inspectorWidth);
        SplitterColumn.Width = hasPreview && !compact ? new GridLength(16) : new GridLength(0);
        PreviewSplitter.Visibility = hasPreview && !compact ? Visibility.Visible : Visibility.Collapsed;
        PreviewBackButton.Visibility = detailOnly ? Visibility.Visible : Visibility.Collapsed;
        SearchHeadingRow.Visibility = detailOnly ? Visibility.Collapsed : Visibility.Visible;
        PaginationBar.Visibility = detailOnly || _status.FileCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        var timedMedia = _preview?.Kind is FileKind.Video or FileKind.Audio;
        var mediaHeight = timedMedia
            ? Math.Min(ActualHeight < 650 ? 104 : ActualHeight < 820 ? 180 : 240, Math.Max(64, ResultsArea.ActualHeight - 260))
            : Math.Min(ActualHeight < 650 ? 112 : 210, Math.Max(40, ResultsArea.ActualHeight - 192));
        PreviewMediaRow.Height = new GridLength(_preview?.Kind == FileKind.Document && PreviewImage.Source is null ? 0 : mediaHeight);
        MomentsHeading.Margin = timedMedia && ActualHeight < 820 ? new Thickness(0, 8, 0, 8) : new Thickness(0, 16, 0, 12);
        PersonInspector.Visibility = _personInspectorOpen ? Visibility.Visible : Visibility.Collapsed;
        PersonInspectorColumn.Width = !_personInspectorOpen ? new GridLength(0) : compact ? new GridLength(1, GridUnitType.Star) : new GridLength(340);
        PeopleGalleryColumn.Width = _personInspectorOpen && compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        PeopleResultsPanel.Visibility = _personInspectorOpen && compact ? Visibility.Collapsed : Visibility.Visible;
        PersonBackButton.Visibility = _personInspectorOpen && compact ? Visibility.Visible : Visibility.Collapsed;
        PersonInspector.Margin = new Thickness(_personInspectorOpen && !compact ? 12 : 0, 0, 0, 0);
        foreach (var button in new[] { SearchNav, PeopleNav, FoldersNav, PrepareNav }) { button.MinHeight = 32; button.Padding = new Thickness(10, 6, 10, 6); }
        DriveLocation.Visibility = ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        PortableHint.Padding = ActualHeight < 650 ? new Thickness(10, 8, 8, 8) : new Thickness(10, 12, 8, 10);
        EmptyIcon.Visibility = ActualHeight < 650 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.FontSize = ActualHeight < 650 ? 20 : 22;
        UpdateViewButtons();
        if (restore)
            Dispatcher.BeginInvoke(() => { ArrangeRows(); ScrollGalleryToAnchor(_returnAnchor); }, DispatcherPriority.Loaded);
    }
    private void PreviewResizeStarted(object sender, DragStartedEventArgs e) => _resizingInspector = true;
    private void PreviewResized(object sender, DragCompletedEventArgs e) { _inspectorWidth = Math.Clamp(PreviewColumn.ActualWidth, 300, 480); _resizingInspector = false; ApplyResponsiveLayout(); ArrangeRows(true); }
    private void RestoreResultFocus()
    {
        if (!_uiReady || _lastResultId is not Guid id) return;
        Dispatcher.BeginInvoke(() =>
        {
            var tile = CurrentTiles.FirstOrDefault(t => t.Result.FileId == id);
            if (tile is null) return;
            if (_listMode) { MetadataList.ScrollIntoView(tile); MetadataList.Focus(); return; }
            var index = CurrentTiles.IndexOf(tile);
            if (Rows.ElementAtOrDefault(ContactSheetLayout.GetRowIndex(_sheetRows, index)) is { } row) Gallery.ScrollIntoView(row);
            Dispatcher.BeginInvoke(() => VisualChildren<Button>(Gallery).FirstOrDefault(b => ReferenceEquals(b.DataContext, tile))?.Focus(), DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private async void BrowseFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FolderEntry folder) return;
        Navigate("search"); var initialized = _initialized; _initialized = false;
        FolderFilter.SelectedItem = FolderFilter.Items.OfType<FolderChoice>().FirstOrDefault(f => f.Path == folder.RelativePath);
        _initialized = initialized; await SearchAsync(true);
    }
    private void RefreshPeopleGallery()
    {
        _personTiles.Clear();
        _personTiles.AddRange(_people.Select(p => new PersonTile { Person = p, IsSelected = p.Id == _editingPerson?.Id }));
        _peopleRowsDirty = true;
        ArrangePeopleRows(true);
    }
    private void PeopleSizeChanged(object sender, SizeChangedEventArgs e) => ArrangePeopleRows();
    private void ArrangePeopleRows(bool force = false)
    {
        if (!_uiReady || PeopleResultsPanel.Visibility != Visibility.Visible || PeopleGallery.ActualWidth < 1) return;
        var width = Math.Max(120, PeopleGallery.ActualWidth - 24);
        var columns = Math.Max(1, (int)((width + 12) / 176));
        var tileWidth = Math.Floor((width - (columns - 1) * 12) / columns);
        foreach (var tile in _personTiles) tile.TileWidth = tileWidth;
        if (!force && !_peopleRowsDirty && _peopleColumns == columns) return;
        _peopleRowsDirty = false;
        _peopleColumns = columns; PeopleRows.Clear();
        for (var i = 0; i < _personTiles.Count; i += columns) PeopleRows.Add(new(_personTiles.Skip(i).Take(columns).ToArray()));
    }
    private void PersonTileClick(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is PersonTile tile) { PeopleList.SelectedItem = tile.Person; OpenPersonInspector(); } }
    private void OpenPersonInspector()
    {
        _personInspectorOpen = true;
        foreach (var tile in _personTiles) tile.IsSelected = tile.Person.Id == _editingPerson?.Id;
        ApplyResponsiveLayout(); ArrangePeopleRows();
    }
    private void ClosePersonClick(object sender, RoutedEventArgs e)
    {
        if (!TryLeavePersonEditor()) return;
        _personInspectorOpen = false; ApplyResponsiveLayout(); ArrangePeopleRows(); RestorePersonFocus();
    }
    private void RestorePersonFocus()
    {
        var selected = _editingPerson?.Id;
        Dispatcher.BeginInvoke(() =>
        {
            ArrangePeopleRows();
            if (!PeopleResultsPanel.IsVisible || _personInspectorOpen) return;
            var tile = _personTiles.FirstOrDefault(person => person.Person.Id == selected);
            if (tile is null) { PeopleGallery.Focus(); return; }
            var index = _personTiles.IndexOf(tile);
            if (PeopleRows.ElementAtOrDefault(index / Math.Max(1, _peopleColumns)) is { } row) PeopleGallery.ScrollIntoView(row);
            Dispatcher.BeginInvoke(() =>
            {
                if (!PeopleResultsPanel.IsVisible || _personInspectorOpen) return;
                VisualChildren<Button>(PeopleGallery).FirstOrDefault(button => button.DataContext is PersonTile candidate && candidate.Person.Id == selected)?.Focus();
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }
    private async void PersonTileLoaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PersonTile tile || tile.Portrait is not null || _portraitRequests.ContainsKey(tile)) return;
        var cancellation = new CancellationTokenSource(); _portraitRequests[tile] = cancellation;
        try { var portrait = await _thumbnails.ReadAsync(tile.Person.Portrait, cancellation.Token); if (!cancellation.IsCancellationRequested) tile.Portrait = portrait; }
        catch (OperationCanceledException) { }
        finally { if (_portraitRequests.TryGetValue(tile, out var request) && ReferenceEquals(request, cancellation)) _portraitRequests.Remove(tile); cancellation.Dispose(); }
    }
    private void PersonTileUnloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PersonTile tile) return;
        if (_portraitRequests.Remove(tile, out var request)) request.Cancel();
        tile.Portrait = null;
    }
    private void StartPlaybackPolling()
    {
        _playbackTimer.Stop(); _playbackTimer.Tick -= PlaybackTick; _playbackTimer.Tick += PlaybackTick; _playbackTimer.Start();
    }
    private void StopPlaybackPolling() => _playbackTimer.Stop();
    private async void PlaybackTick(object? sender, EventArgs e)
    {
        if (_pollingPlayback || _player is null || _selectedResult is null) return;
        _pollingPlayback = true;
        var player = _player; var selected = _selectedResult;
        try
        {
            var state = await player.GetStateAsync();
            if (!ReferenceEquals(player, _player) || !ReferenceEquals(selected, _selectedResult)) return;
            PreviewTime.Text = $"{FormatTime(state.PositionMs)} / {FormatTime(state.DurationMs)}";
            PlayPauseButton.Content = state.Playing ? "Pause" : "Play";
            AutomationProperties.SetName(PlayPauseButton, state.Playing ? "Pause playback" : "Play playback");
            PlaybackSeek.Maximum = Math.Max(1, state.DurationMs);
            if (!_seeking) PlaybackSeek.Value = state.PositionMs;
            PlaybackSeek.IsEnabled = state.DurationMs > 0;
        }
        catch (Exception) { StopPlaybackPolling(); }
        finally { _pollingPlayback = false; }
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => _seeking = true;
    private async void SeekReleased(object sender, MouseButtonEventArgs e) { _seeking = false; await SeekToSliderAsync(); }
    private void SeekKeyStarted(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) _seeking = true; }
    private async void SeekKeyReleased(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) { _seeking = false; await SeekToSliderAsync(); } }
    private async Task SeekToSliderAsync()
    {
        if (_player is null) return;
        try { await _player.SeekAsync((long)PlaybackSeek.Value); ShowDecodedVideo(); }
        catch (Exception ex) { Notice($"Could not seek: {ex.Message}"); }
    }
}
