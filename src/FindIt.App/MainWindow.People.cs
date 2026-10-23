using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FindIt.Shared;
using Microsoft.Win32;

namespace FindIt.App;

public partial class MainWindow
{
    private bool _suppressPersonSelection, _savingPerson, _loadingFaceReview;
    private string _personOriginalName = "", _personOriginalAliases = "";
    private Guid _personDraftLibraryId;
    private int _peopleLoadVersion;
    private bool HasUnsavedPersonChanges => _personInspectorOpen &&
        (PersonName.Text != _personOriginalName || PersonAliases.Text != _personOriginalAliases || _references.Length > 0);

    private void RememberPersonDraft()
    {
        _personOriginalName = PersonName.Text;
        _personOriginalAliases = PersonAliases.Text;
    }

    // Used by navigation, the inspector close action, and application shutdown.
    private bool TryLeavePersonEditor()
    {
        if (!_personInspectorOpen) return true;
        if (_savingPerson || _loadingFaceReview) { Notice(_savingPerson ? "Wait for this person to finish saving." : "Wait for face matches to finish loading."); return false; }
        if (!HasUnsavedPersonChanges) return true;
        if (!ThemedDialog.ConfirmDiscard(this, "Your changes to this person's name, aliases, or reference photos have not been saved.", "Unsaved person changes")) return false;
        PersonName.Text = _personOriginalName; PersonAliases.Text = _personOriginalAliases;
        _references = []; ReferenceCount.Text = "No new photos selected"; ReferenceCount.ToolTip = null;
        return true;
    }

    private bool PersonEditorMatchesArchive(Window? owner = null)
    {
        if (_personDraftLibraryId == Guid.Empty || _status.LibraryId == Guid.Empty || _personDraftLibraryId == _status.LibraryId) return true;
        const string message = "The archive changed. Reopen this person from the current archive before saving or reviewing matches. Your draft is still here.";
        if (owner is null) Notice(message); else ThemedDialog.Show(owner, message, "Archive changed", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task LoadPeopleAsync()
    {
        if (_backend is null) return;
        var libraryId = _status.LibraryId;
        var request = ++_peopleLoadVersion;
        try
        {
            var people = await _backend.GetPeopleAsync();
            if (_closing || libraryId != _status.LibraryId || request != _peopleLoadVersion) return;
            _people = people;
            // Rebinding the hidden selection list must not reset an in-progress draft.
            _suppressPersonSelection = true;
            try { PeopleList.ItemsSource = _people; PeopleList.SelectedItem = _people.FirstOrDefault(p => p.Id == _editingPerson?.Id); }
            finally { _suppressPersonSelection = false; }
            RefreshPeopleGallery();
            PeopleEmpty.Visibility = _people.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var selectedPerson = PersonFilter.SelectedItem as PersonChip;
            var selected = selectedPerson?.PersonId ?? _filters.PersonId;
            var chips = new[] { new PersonChip(Guid.Empty, "Anyone") }.Concat(_people.Select(p => new PersonChip(p.Id, p.Name))).ToArray();
            if (selectedPerson is { PersonId: var selectedId } && selectedId != Guid.Empty && !chips.Any(p => p.PersonId == selectedId)) chips = chips.Append(selectedPerson).ToArray();
            var wasInitialized = _initialized; _initialized = false;
            PersonFilter.ItemsSource = chips; PersonFilter.SelectedItem = chips.FirstOrDefault(p => p.PersonId == selected) ?? chips[0];
            _initialized = wasInitialized; CaptureFilterState(); UpdateFilterUi();
        }
        catch (Exception ex) { if (!_closing && request == _peopleLoadVersion) Notice($"Could not read people: {ex.Message}"); }
    }

    private void DisplayPersonEditor(PersonRecord? person)
    {
        _editingPerson = person; _personDraftLibraryId = _status.LibraryId;
        _suppressPersonSelection = true;
        try { PeopleList.SelectedItem = person is null ? null : _people.FirstOrDefault(p => p.Id == person.Id); }
        finally { _suppressPersonSelection = false; }
        var named = person?.Confirmed == true;
        PersonHeading.Text = person is null ? "Add a person" : named ? person.Name : "Who is this?";
        PersonInitials.Text = person is null ? "+" : new PersonTile { Person = person }.Initials;
        PersonAvatar.ImageSource = null;
        if (person?.Portrait is { } portrait) _ = FillAvatarAsync(person.Id, portrait);
        PersonName.Text = person is null || !named ? "" : person.Name; PersonAliases.Text = person is null ? "" : string.Join(", ", person.Aliases);
        _references = []; ReferenceCount.Text = "No new photos selected"; ReferenceCount.ToolTip = null;
        PersonObservations.Text = person is null ? "Choose clear photos of their face, then save. FindIt looks for them the next time this drive is prepared."
            : named ? $"Seen {person.ObservationCount:N0} time{(person.ObservationCount == 1 ? "" : "s")}{(person.Aliases.Count > 0 ? " · also known as " + string.Join(", ", person.Aliases) : "")}"
            : $"A group of {person.ObservationCount:N0} similar face{(person.ObservationCount == 1 ? "" : "s")} found while preparing. Add a name to search for them.";
        SearchPersonButton.Content = named ? $"Find {person!.Name}" : "Find this person";
        SearchPersonButton.IsEnabled = ReviewFacesButton.IsEnabled = MergePersonButton.IsEnabled = person is not null;
        PersonReviewCard.Visibility = person is null ? Visibility.Collapsed : Visibility.Visible;
        RememberPersonDraft(); OpenPersonInspector();
        _ = LoadPersonSuggestionsAsync(person);
    }

    private async Task FillAvatarAsync(Guid personId, BlobReference portrait)
    {
        try { var image = await _thumbnails.ReadAsync(portrait, CancellationToken.None); if (_editingPerson?.Id == personId) PersonAvatar.ImageSource = image; }
        catch (Exception) { /* A missing portrait keeps the initials. */ }
    }

    private int _suggestionVersion;
    private bool _savingSuggestion;
    // Up to twelve uncertain faces, best quality first, for a quick yes or no without the full review.
    private async Task LoadPersonSuggestionsAsync(PersonRecord? person)
    {
        var version = ++_suggestionVersion;
        PersonSuggestionList.ItemsSource = null; PersonSuggestionEmpty.Visibility = Visibility.Collapsed;
        if (_backend is null || person is null) return;
        try
        {
            var observations = await _backend.GetPersonObservationsAsync(person.Id);
            if (version != _suggestionVersion) return;
            var label = person.Confirmed ? person.Name : "this person";
            var suggestions = observations.Where(o => !o.Confirmed && !o.Rejected).OrderByDescending(o => o.Quality).Take(12).Select(o => new PersonSuggestion(o, label)).ToArray();
            PersonSuggestionList.ItemsSource = suggestions;
            PersonSuggestionEmpty.Visibility = suggestions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PersonReviewHint.Text = suggestions.Length == 0 ? "Nothing waiting. Use Review all matches to change earlier answers." : $"Is this {label}? Your answers stay on this drive and make person searches more reliable.";
            foreach (var suggestion in suggestions)
            {
                var image = await _thumbnails.ReadAsync(suggestion.Observation.Thumbnail, CancellationToken.None);
                if (version != _suggestionVersion) return;
                suggestion.Thumbnail = image;
            }
        }
        catch (Exception ex) { if (version == _suggestionVersion) Notice($"Could not load suggested matches: {ex.Message}"); }
    }

    private async void ConfirmSuggestionClick(object sender, RoutedEventArgs e) => await DecideSuggestionAsync(sender, SuggestionDecision.Confirmed);
    private async void RejectSuggestionClick(object sender, RoutedEventArgs e) => await DecideSuggestionAsync(sender, SuggestionDecision.Rejected);
    private async Task DecideSuggestionAsync(object sender, SuggestionDecision decision)
    {
        if (_backend is null || _editingPerson is not { } person || _savingSuggestion || _savingPerson || (sender as FrameworkElement)?.Tag is not PersonSuggestion suggestion || suggestion.Decision == decision || !PersonEditorMatchesArchive()) return;
        _savingSuggestion = true; PersonSuggestionList.IsEnabled = false;
        try
        {
            var id = suggestion.Observation.Id;
            await _backend.SavePersonAsync(new() { PersonId = person.Id, Name = person.Name, Aliases = person.Aliases,
                ConfirmObservationIds = decision == SuggestionDecision.Confirmed ? [id] : [], RejectObservationIds = decision == SuggestionDecision.Rejected ? [id] : [] });
            suggestion.Decision = decision;
        }
        catch (Exception ex) { Notice($"Could not save that answer: {ex.Message}"); }
        finally { _savingSuggestion = false; PersonSuggestionList.IsEnabled = true; }
    }

    private void AddPersonClick(object sender, RoutedEventArgs e)
    {
        if (!TryLeavePersonEditor()) return;
        DisplayPersonEditor(null); PersonName.Focus();
    }

    private void PersonSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPersonSelection || PeopleList.SelectedItem is not PersonRecord person) return;
        if (_personInspectorOpen && person.Id == _editingPerson?.Id && _personDraftLibraryId == _status.LibraryId) return;
        if (!TryLeavePersonEditor())
        {
            _suppressPersonSelection = true;
            try { PeopleList.SelectedItem = _people.FirstOrDefault(p => p.Id == _editingPerson?.Id); }
            finally { _suppressPersonSelection = false; }
            return;
        }
        DisplayPersonEditor(person);
    }

    private void ReferencePhotosClick(object sender, RoutedEventArgs e)
    {
        if (_savingPerson) return;
        var dialog = new OpenFileDialog { Title = "Choose reference photos on this drive", InitialDirectory = _root, Multiselect = true, AddToRecent = false, RestoreDirectory = true, Filter = "Photos|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.heic;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Length > 32) { Notice("Choose at most 32 reference photos at a time."); return; }
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (dialog.FileNames.Any(path => !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        { Notice("Choose reference photos stored in this archive, so the photos travel with your drive."); return; }
        _references = dialog.FileNames; ReferenceCount.Text = $"{_references.Length} photo{(_references.Length == 1 ? "" : "s")} selected";
        ReferenceCount.ToolTip = string.Join(Environment.NewLine, _references.Select(Path.GetFileName));
    }

    private async void SavePersonClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || _savingPerson || _loadingFaceReview || !PersonEditorMatchesArchive()) return;
        var name = PersonName.Text.Trim();
        if (name.Length is 0 or > 160 || name.Any(char.IsControl)) { Notice("Enter a name between 1 and 160 characters without control characters."); PersonName.Focus(); return; }
        var aliases = PersonAliases.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (aliases.Length > 32 || aliases.Any(alias => alias.Length > 160 || alias.Any(char.IsControl))) { Notice("Use at most 32 aliases, each up to 160 characters without control characters."); PersonAliases.Focus(); return; }
        if (_editingPerson is null && _references.Length == 0) { Notice("Choose at least one reference photo for a new person."); return; }
        var libraryId = _status.LibraryId;
        var request = new PersonUpdateRequest { PersonId = _editingPerson?.Id, Name = name, Aliases = aliases, ReferencePaths = _references.ToArray() };
        var button = (Button)sender; var label = button.Content;
        _savingPerson = true; PersonInspector.IsEnabled = false; PeopleGallery.IsEnabled = false; button.Content = "Saving…";
        try
        {
            var saved = await _backend.SavePersonAsync(request);
            if (libraryId != _status.LibraryId) { PersonEditorMatchesArchive(); return; }
            await LoadPeopleAsync();
            if (libraryId != _status.LibraryId) { PersonEditorMatchesArchive(); return; }
            DisplayPersonEditor(saved);
            Notice($"Saved {saved.Name}. Future preparation uses these references.");
        }
        catch (Exception ex) { Notice($"Could not save this person: {ex.Message}"); }
        finally { _savingPerson = false; PersonInspector.IsEnabled = true; PeopleGallery.IsEnabled = true; button.Content = label; }
    }
    private async void SearchPersonClick(object sender, RoutedEventArgs e)
    {
        if (_editingPerson is null || _savingPerson || _loadingFaceReview || !PersonEditorMatchesArchive()) return;
        Navigate("search"); _initialized = false;
        PersonFilter.SelectedItem = PersonFilter.Items.OfType<PersonChip>().FirstOrDefault(p => p.PersonId == _editingPerson.Id); _initialized = true; await SearchAsync(true);
    }

    private async void ReviewFacesClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || _editingPerson is null || !PersonEditorMatchesArchive() || !TryLeavePersonEditor()) return;
        var person = _editingPerson;
        _loadingFaceReview = true; PersonInspector.IsEnabled = false; PeopleGallery.IsEnabled = false;
        try
        {
            var observations = await _backend.GetPersonObservationsAsync(person.Id);
            if (!PersonEditorMatchesArchive()) return;
            var ordered = observations.OrderBy(o => o.Confirmed || o.Rejected).ToArray();
            var panel = new DockPanel { Margin = new Thickness(22) };
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = $"Face matches for {person.Name}", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 90, Margin = new(0, 0, 0, 10) });
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
            var closeReview = new Button { Content = "Close", IsCancel = true, MinWidth = 80, Margin = new(8, 0, 0, 0) }; DockPanel.SetDock(closeReview, Dock.Right); actions.Children.Add(closeReview);
            var save = new Button { Content = "Save corrections", HorizontalAlignment = HorizontalAlignment.Right }; save.SetResourceReference(StyleProperty, "PrimaryButton"); DockPanel.SetDock(save, Dock.Right); actions.Children.Add(save);
            var previous = new Button { Content = "Previous", Margin = new(0, 0, 8, 0) }; var next = new Button { Content = "Next" };
            var navigation = new StackPanel { Orientation = Orientation.Horizontal }; navigation.Children.Add(previous); navigation.Children.Add(next); actions.Children.Add(navigation);
            var rows = new StackPanel(); var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; panel.Children.Add(scroll);
            var decisions = new Dictionary<long, int>();
            var selected = new HashSet<long>();
            var page = 0; const int pageSize = 40;
            var savingCorrections = false;
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
            closeReview.Click += (_, _) => dialog.Close();
            dialog.Closed += (_, _) => { pageImages?.Cancel(); pageImages?.Dispose(); };
            dialog.Closing += (_, e) =>
            {
                if (savingCorrections) { e.Cancel = true; return; }
                if ((selected.Count > 0 || decisions.Any(decision => decision.Value is 1 or 2)) && !ThemedDialog.ConfirmDiscard(dialog, "Your selected faces or corrections have not been saved.", "Unsaved face corrections")) e.Cancel = true;
            };
            move.Click += async (_, _) =>
            {
                if (!PersonEditorMatchesArchive(dialog)) return;
                var name = newName.Text.Trim();
                if (name.Length is 0 or > 160 || name.Any(char.IsControl)) { ThemedDialog.Show(dialog, "Enter a name between 1 and 160 characters without control characters.", "Move selected faces"); newName.Focus(); return; }
                var ids = selected.ToArray(); if (ids.Length == 0) return;
                if (savingCorrections) return;
                savingCorrections = true; panel.IsEnabled = false; move.Content = "Moving…";
                try
                {
                    var saved = await _backend.SavePersonAsync(new() { PersonId = null, Name = name, ConfirmObservationIds = ids });
                    if (!PersonEditorMatchesArchive(dialog)) return;
                    ordered = ordered.Where(observation => !selected.Contains(observation.Id)).ToArray();
                    foreach (var id in ids) decisions.Remove(id);
                    selected.Clear(); newName.Clear(); ShowPage();
                    await LoadPeopleAsync();
                    if (!PersonEditorMatchesArchive(dialog)) return;
                    if (_people.FirstOrDefault(item => item.Id == person.Id) is { } updated) DisplayPersonEditor(updated);
                    Notice($"Moved {ids.Length:N0} face{(ids.Length == 1 ? "" : "s")} to {saved.Name}.");
                }
                catch (Exception ex) { ThemedDialog.Show(dialog, ex.Message, "Move selected faces"); }
                finally { savingCorrections = false; panel.IsEnabled = true; move.Content = "Move selected"; }
            };
            save.Click += async (_, _) =>
            {
                if (!PersonEditorMatchesArchive(dialog)) return;
                if (savingCorrections || selected.Count > 0 || !decisions.Any(decision => decision.Value is 1 or 2)) return;
                savingCorrections = true; panel.IsEnabled = false; save.Content = "Saving…";
                try
                {
                    await _backend.SavePersonAsync(new() { PersonId = person.Id, Name = person.Name, Aliases = person.Aliases, ConfirmObservationIds = decisions.Where(d => d.Value == 1).Select(d => d.Key).ToArray(), RejectObservationIds = decisions.Where(d => d.Value == 2).Select(d => d.Key).ToArray() });
                    await LoadPeopleAsync();
                    if (!PersonEditorMatchesArchive(dialog)) return;
                    if (_people.FirstOrDefault(item => item.Id == person.Id) is { } updated) DisplayPersonEditor(updated);
                    decisions.Clear(); selected.Clear(); savingCorrections = false; dialog.Close(); Notice("Face corrections saved.");
                }
                catch (Exception ex) { ThemedDialog.Show(dialog, ex.Message, "Save corrections"); }
                finally { savingCorrections = false; panel.IsEnabled = true; save.Content = "Save corrections"; }
            };
            ShowPage();
            dialog.ShowDialog();
        }
        catch (Exception ex) { Notice($"Could not load face matches: {ex.Message}"); }
        finally { _loadingFaceReview = false; PersonInspector.IsEnabled = true; PeopleGallery.IsEnabled = true; }
    }
    private async Task FillPortraitAsync(Image image, BlobReference? reference, CancellationToken token = default) { try { image.Source = await _thumbnails.ReadAsync(reference, token); } catch (Exception) { /* Missing portraits do not block corrections. */ } }
    private async void PersonPortraitLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: PersonRecord person } image) await FillPortraitAsync(image, person.Portrait);
    }
    private async void MomentThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: MomentGroup moment } image) { image.Visibility = moment.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible; await FillPortraitAsync(image, moment.Thumbnail); }
    }
    private void MergePersonClick(object sender, RoutedEventArgs e)
    {
        if (_backend is null || _editingPerson is null || !PersonEditorMatchesArchive() || !TryLeavePersonEditor()) return;
        var source = _editingPerson;
        var choices = _people.Where(p => p.Id != source.Id).ToArray();
        if (choices.Length == 0) { Notice("Add another person before merging names."); return; }
        var panel = new Grid { Margin = new(24) };
        panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = $"Merge {source.Name} into", FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new(0, 20, 0, 15) };
        AutomationProperties.SetName(combo, "Merge target person"); body.Children.Add(combo);
        body.Children.Add(new TextBlock { Text = "Their reference photos and face matches will be combined.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) }; Grid.SetRow(actions, 1); panel.Children.Add(actions);
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new(0, 0, 8, 0) }; actions.Children.Add(cancel);
        var button = new Button { Content = "Merge people" }; button.SetResourceReference(StyleProperty, "PrimaryButton"); actions.Children.Add(button);
        var dialog = CreateDialog("Merge people", panel, 440, 320);
        var merging = false;
        dialog.Loaded += (_, _) => combo.Focus();
        dialog.Closing += (_, e) => { if (merging) e.Cancel = true; };
        cancel.Click += (_, _) => dialog.Close();
        button.Click += async (_, _) =>
        {
            if (!PersonEditorMatchesArchive(dialog)) return;
            if (merging || combo.SelectedItem is not PersonRecord target) return;
            merging = true; panel.IsEnabled = false; button.Content = "Merging…";
            try
            {
                await _backend.SavePersonAsync(new() { PersonId = source.Id, Name = source.Name, MergeIntoPersonId = target.Id });
                if (!PersonEditorMatchesArchive(dialog)) return;
                await LoadPeopleAsync();
                if (!PersonEditorMatchesArchive(dialog)) return;
                if (_people.FirstOrDefault(person => person.Id == target.Id) is { } updated) DisplayPersonEditor(updated);
                merging = false; dialog.Close();
                Notice($"Merged {source.Name} into {target.Name}.");
            }
            catch (Exception ex) { ThemedDialog.Show(dialog, ex.Message, "Merge people"); }
            finally { merging = false; panel.IsEnabled = true; button.Content = "Merge people"; }
        };
        dialog.ShowDialog();
    }
    private Window CreateDialog(string title, object content, double width, double height)
    {
        var dialog = new Window { Owner = this, Title = title, Content = content, Width = width, Height = height, MinWidth = 350, MinHeight = 250, MaxWidth = Math.Max(350, SystemParameters.WorkArea.Width - 24), MaxHeight = Math.Max(250, SystemParameters.WorkArea.Height - 24), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        ThemedDialog.ApplyWindowAppearance(dialog);
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { dialog.Close(); e.Handled = true; } };
        return dialog;
    }

}
