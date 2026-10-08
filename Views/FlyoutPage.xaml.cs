using System.Globalization;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using TrayMDB.Services;
using TrayMDB.Tmdb;
using Launcher = Windows.System.Launcher;
using VirtualKey = Windows.System.VirtualKey;

namespace TrayMDB.Views;

/// <summary>
/// The flyout's content: a search box over trending titles or search results, and a detail view
/// for the selected title or person, with a back history as the user follows cast and credits.
/// "My stuff" swaps the search box for a filter over the titles the user marked seen or want to
/// watch (<see cref="LibraryService"/>). Nothing is fetched while the flyout is hidden: trending refreshes through
/// <see cref="ForegroundPoller"/>, and searches and detail loads are cancelled on hide.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    private static readonly TimeSpan TrendingInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);
    private const int MaxCachedDetails = 20;
    private const int MaxCast = 12;
    private const int MaxCrew = 8;
    private const int MaxKnownFor = 12;
    private const int MaxHistory = 20;
    private static readonly Lazy<ResourceLoader> s_resources = new(() => new ResourceLoader());

    private ForegroundPoller _trendingPoller;
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly Dictionary<(MediaKind, int), object> _detailsCache = []; // TmdbDetails or TmdbPerson
    private readonly List<TmdbSearchItem> _history = [];

    private List<TmdbSearchItem> _trending = [];
    private List<TmdbSearchItem> _searchResults = [];
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;
    private int _searchGeneration;
    private string? _shownQuery;
    private string? _keyInUse;
    private TmdbSearchItem? _detailItem;
    private TmdbDetails? _details;
    private TmdbPerson? _person;
    private bool _showingLibrary;
    private LibraryFilter _libraryFilter;

    public FlyoutPage()
    {
        InitializeComponent();
        TitleText.Text = App.DisplayName;

        _trendingPoller = CreatePoller();

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = SearchDelay;
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = RunSearchAsync(SearchBox.Text);
    }

    public bool IsShowingDetails => DetailView.Visibility == Visibility.Visible;

    internal Action? BackRequested { get; set; }

    internal Action? HomeRequested { get; set; }

    internal void ReturnToSearch() => ShowSearchView();

    internal void OpenRandomPick(RandomPick pick)
    {
        _history.Clear();
        if (_detailsCache.Count >= MaxCachedDetails)
        {
            _detailsCache.Clear();
        }

        _detailsCache[(pick.Details.Kind, pick.Item.Id)] = pick.Details;
        OpenDetails(pick.Item, addToHistory: false);
    }

    private void RandomButton_Click(object sender, RoutedEventArgs e) =>
        App.Current.ShowRandom();

    public static Visibility VisibleIf(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static string JoinParts(string? a, string? b) => TmdbFormat.JoinParts(a, b);

    public static string WatchGroupLabel(WatchOfferKind kind) => s_resources.Value.GetString($"Watch{kind}");

    /// <summary>Segoe Fluent Icons: Contact for a person, Video for a title.</summary>
    public static string PlaceholderGlyph(bool isPerson) => isPerson ? "" : "";

    /// <summary>The flyout opened (or Settings closed): pick up key changes, refresh what's stale.</summary>
    public void OnShown()
    {
        string? key = TmdbService.ActiveKey;
        if (key != _keyInUse)
        {
            // New, changed, or removed key: everything fetched with the old one is suspect.
            _keyInUse = key;
            _trending = [];
            _shownQuery = null;
            _detailsCache.Clear();
            _trendingPoller.Stop();
            _trendingPoller = CreatePoller(); // Forgets when it last succeeded, so Start fetches.
        }

        if (key is null)
        {
            ShowMissingKey();
            return;
        }

        _trendingPoller.Start();

        if (_showingLibrary)
        {
            ShowLibrary();
        }
        else
        {
            // A search cancelled by the last hide (or never shown) runs again now.
            string query = SearchBox.Text.Trim();
            if (query.Length > 0 && query != _shownQuery)
            {
                _ = RunSearchAsync(query);
            }
            else if (query.Length == 0)
            {
                ShowTrending();
            }
        }

        if (IsShowingDetails && _details is null && _person is null && _detailItem is not null)
        {
            _ = LoadDetailsAsync(_detailItem);
        }

        FocusCurrentView();
        if (IsShowingDetails && WatchExpander.IsExpanded)
        {
            ShowWatchProviders();
        }
    }

    /// <summary>The flyout hid: stop the poller and abandon requests in flight.</summary>
    public void OnHidden()
    {
        _trendingPoller.Stop();
        _searchTimer.Stop();
        _searchCts?.Cancel();
        _detailsCts?.Cancel();
        WatchGroupsRepeater.ItemsSource = null;
    }

    /// <summary>
    /// Escape, Alt+Left, and the mouse back button step back through the titles and people opened
    /// from this one, then to the search list.
    /// </summary>
    public bool TryGoBack()
    {
        if (!IsShowingDetails)
        {
            if (_showingLibrary)
            {
                SetLibraryMode(false);
                return true;
            }

            return false;
        }

        if (_history.Count > 0)
        {
            TmdbSearchItem previous = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            OpenDetails(previous, addToHistory: false);
        }
        else
        {
            ShowSearchView();
        }

        return true;
    }

    public void Dispose()
    {
        OnHidden();
        _detailsCache.Clear();
    }

    private ForegroundPoller CreatePoller()
    {
        ForegroundPoller poller = new(DispatcherQueue, TrendingInterval, RefreshTrendingAsync);
        poller.Failed += (_, ex) => ShowError(ex);
        return poller;
    }

    // Search

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        if (SearchBox.Text.Trim().Length == 0)
        {
            _searchCts?.Cancel();
            _shownQuery = null;
            ShowTrending();
            return;
        }

        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                _searchTimer.Stop();
                string query = SearchBox.Text.Trim();
                if (query.Length > 0 && query == _shownQuery && ResultsList.Items.Count > 0)
                {
                    // Results are already showing: Enter opens the top one.
                    OpenDetails((TmdbSearchItem)ResultsList.Items[0]);
                }
                else
                {
                    _ = RunSearchAsync(query);
                }

                break;

            case VirtualKey.Down when ResultsList.Items.Count > 0:
                e.Handled = true;
                if (ResultsList.ContainerFromIndex(0) is ListViewItem first)
                {
                    first.Focus(FocusState.Keyboard);
                }

                break;
        }
    }

    private async Task RunSearchAsync(string text)
    {
        string query = text.Trim();
        if (query.Length == 0)
        {
            return;
        }

        if (TmdbService.CreateClient() is not { } client)
        {
            ShowMissingKey();
            return;
        }

        _searchCts?.Cancel();
        using CancellationTokenSource cts = new();
        _searchCts = cts;
        int generation = ++_searchGeneration;

        LoadingRing.IsActive = true;
        if (query != _shownQuery)
        {
            ListHeader.Text = "Searching…";
        }

        try
        {
            List<TmdbSearchItem> results = await client.SearchAsync(query, TmdbService.Language, cts.Token);
            if (generation != _searchGeneration || SearchBox.Text.Trim() != query)
            {
                return; // A newer search (or a cleared box) owns the list now.
            }

            _shownQuery = query;
            _searchResults = results;
            StatusBar.IsOpen = false;
            ShowSearchResults();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded or hidden; OnShown re-runs it if needed.
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                ShowError(ex);
            }
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                LoadingRing.IsActive = false;
                _searchCts = null;
            }
        }
    }

    private async Task RefreshTrendingAsync(CancellationToken cancellationToken)
    {
        if (TmdbService.CreateClient() is not { } client)
        {
            ShowMissingKey();
            return;
        }

        bool showing = !_showingLibrary && SearchBox.Text.Trim().Length == 0;
        if (showing && _trending.Count == 0)
        {
            CenterRing.IsActive = true;
        }

        try
        {
            _trending = await client.TrendingAsync(TmdbService.Language, cancellationToken);
            StatusBar.IsOpen = false;
            if (SearchBox.Text.Trim().Length == 0)
            {
                ShowTrending();
            }
        }
        finally
        {
            CenterRing.IsActive = false;
        }
    }

    private void ShowTrending()
    {
        if (_showingLibrary)
        {
            return; // Picked up when the user switches back.
        }

        if (_keyInUse is null)
        {
            ShowMissingKey();
            return;
        }

        ShowList(_trending, _trending.Count == 0 ? string.Empty : "Trending today");
    }

    private void ShowSearchResults()
    {
        if (_showingLibrary || _shownQuery is null)
        {
            return;
        }

        ShowList(_searchResults, _searchResults.Count == 0 ? string.Empty : "Results");
        if (_searchResults.Count == 0)
        {
            ShowMessage($"No movies or shows match “{_shownQuery}”.");
        }
    }

    private void ShowList<T>(List<T> items, string header)
    {
        ResultsList.ItemTemplate = (DataTemplate)Resources[typeof(T) == typeof(LibraryEntry) ? "LibraryTemplate" : "ResultTemplate"];
        ResultsList.ItemsSource = items;
        ListHeader.Text = header;
        ListHeader.Visibility = VisibleIf(header);
        MessagePanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
        ListHeader.Visibility = Visibility.Collapsed;
    }

    private void ShowMissingKey()
    {
        _showingLibrary = false;
        ShowModeChrome();
        ShowSearchView();
        StatusBar.IsOpen = false;
        CenterRing.IsActive = false;
        ShowMessage("This build has no TMDB API key. Add TmdbApiKey to OAuth.resw and rebuild.");
    }

    private void ShowError(Exception ex)
    {
        if (ex is TmdbAuthException)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "TMDB didn't accept the API key";
            StatusBar.Message = "Check TmdbApiKey in OAuth.resw.";
        }
        else
        {
            bool online = ConnectivityService.IsInternetAvailable;
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = online ? "Can't reach TMDB" : "No internet connection";
            StatusBar.Message = ex is HttpRequestException or TaskCanceledException
                ? online ? "Try again in a moment." : "Search works again when you're back online."
                : ex.Message;
        }

        StatusBar.IsOpen = true;
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        TmdbSearchItem? item = e.ClickedItem switch
        {
            TmdbSearchItem result => result,
            LibraryEntry entry => entry.ToSearchItem(),
            _ => null,
        };
        if (item is not null)
        {
            _history.Clear(); // A fresh pick from the list starts a new trail.
            OpenDetails(item, addToHistory: false);
        }
    }

    // ItemsRepeater doesn't set DataContext for x:Bind templates, so each tile carries its item in Tag.
    private void CastMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TmdbCastMember member } && member.Id > 0)
        {
            OpenDetails(member.ToPersonItem());
        }
    }

    private void KnownForTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TmdbSearchItem item })
        {
            OpenDetails(item);
        }
    }

    // Details

    /// <param name="addToHistory">True when following a link from the current page (a cast member or
    /// a known-for title), so Back returns to it.</param>
    private void OpenDetails(TmdbSearchItem item, bool addToHistory = true)
    {
        if (addToHistory && _detailItem is not null)
        {
            _history.Add(_detailItem);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        HomeButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _detailItem = item;
        _details = null;
        _person = null;
        ShowPreview(item);

        SearchView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;
        DetailScroller.ChangeView(null, 0, null, disableAnimation: true);
        BackButton.Focus(FocusState.Programmatic);

        _ = LoadDetailsAsync(item);
    }

    private async Task LoadDetailsAsync(TmdbSearchItem item)
    {
        if (item.Kind is not { } kind)
        {
            return;
        }

        if (_detailsCache.TryGetValue((kind, item.Id), out object? cached))
        {
            ShowLoaded(cached);
            return;
        }

        if (TmdbService.CreateClient() is not { } client)
        {
            ShowMissingKey();
            return;
        }

        _detailsCts?.Cancel();
        using CancellationTokenSource cts = new();
        _detailsCts = cts;
        DetailRing.IsActive = true;
        DetailRing.Visibility = Visibility.Visible;
        DetailStatusBar.IsOpen = false;

        try
        {
            object loaded = kind == MediaKind.Person
                ? await client.GetPersonAsync(item.Id, TmdbService.Language, cts.Token)
                : await client.GetDetailsAsync(kind, item.Id, TmdbService.Language, cts.Token);
            if (_detailsCache.Count >= MaxCachedDetails)
            {
                _detailsCache.Clear();
            }

            _detailsCache[(kind, item.Id)] = loaded;
            if (ReferenceEquals(_detailItem, item))
            {
                ShowLoaded(loaded);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Went back, opened another page, or hid the flyout.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_detailItem, item))
            {
                bool online = ConnectivityService.IsInternetAvailable;
                DetailStatusBar.Title = ex is TmdbAuthException ? "TMDB didn't accept the API key" : online ? "Can't load the details" : "No internet connection";
                DetailStatusBar.Message = ex is TmdbAuthException ? "Check TmdbApiKey in OAuth.resw." :"Showing what the search returned.";
                DetailStatusBar.IsOpen = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_detailsCts, cts))
            {
                _detailsCts = null;
                DetailRing.IsActive = false;
                DetailRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowLoaded(object loaded)
    {
        switch (loaded)
        {
            case TmdbDetails details:
                ShowDetails(details);
                break;
            case TmdbPerson person:
                ShowPerson(person);
                break;
        }
    }

    /// <summary>Fills the detail view from the search row, so it's never blank while details load.</summary>
    private void ShowPreview(TmdbSearchItem item)
    {
        bool isPerson = item.Kind == MediaKind.Person;
        DetailHeaderText.Text = item.DisplayTitle;
        DetailTitle.Text = item.DisplayTitle;
        DetailMeta.Text = item.Subtitle;
        SetRating(item.VoteAverage, item.VoteCount);
        SetText(DetailGenres, null);
        SetText(DetailTagline, null);
        SetText(DetailOverview, isPerson ? null : item.Overview);
        SetText(DetailCredit, null);
        SetText(DetailWatch, null);
        DetailStatusBar.IsOpen = false;
        SetImage(PosterImage, item.TileUri, 92);
        SetImage(BackdropImage, null, 376);
        BackdropBorder.Visibility = Visibility.Collapsed;
        CastRepeater.ItemsSource = null;
        CastSection.Visibility = Visibility.Collapsed;
        CrewRepeater.ItemsSource = null;
        CrewSection.Visibility = Visibility.Collapsed;
        KnownForRepeater.ItemsSource = null;
        KnownForSection.Visibility = Visibility.Collapsed;
        LinksPanel.Visibility = Visibility.Collapsed;
        WatchExpander.IsExpanded = false;
        WatchExpander.Visibility = Visibility.Collapsed;
        WatchGroupsRepeater.ItemsSource = null;
        ShowLibraryFlags();
    }

    private void ShowDetails(TmdbDetails details)
    {
        _details = details;
        string region = TmdbService.Region;

        DetailHeaderText.Text = details.DisplayTitle;
        DetailTitle.Text = details.DisplayTitle;
        DetailMeta.Text = TmdbFormat.MetaLine(details, region);
        SetRating(details.VoteAverage, details.VoteCount);
        SetText(DetailGenres, TmdbFormat.Genres(details));
        SetText(DetailTagline, details.Tagline);
        SetText(DetailOverview, string.IsNullOrWhiteSpace(details.Overview) ? "No overview yet." : details.Overview);
        SetText(DetailCredit, TmdbFormat.Credit(details));

        string watch = TmdbFormat.WatchSummary(details, region);
        SetText(DetailWatch, watch);
        WatchExpander.Visibility = Visibility.Visible;

        Uri? backdrop = TmdbFormat.ImageUri(details.BackdropPath, "w780");
        SetImage(BackdropImage, backdrop, 376);
        BackdropBorder.Visibility = backdrop is null ? Visibility.Collapsed : Visibility.Visible;
        SetImage(PosterImage, TmdbFormat.ImageUri(details.PosterPath, "w185"), 92);

        List<TmdbCastMember> cast = details.Credits?.Cast.Take(MaxCast).ToList() ?? [];
        CastRepeater.ItemsSource = cast;
        CastSection.Visibility = cast.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        List<TmdbCastMember> crew = TmdbFormat.KeyCrew(details, MaxCrew);
        CrewRepeater.ItemsSource = crew;
        CrewSection.Visibility = crew.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        TrailerButton.Visibility = TmdbFormat.TrailerUri(details) is null ? Visibility.Collapsed : Visibility.Visible;
        ImdbButton.Visibility = TmdbFormat.ImdbUri(details.ImdbId) is null ? Visibility.Collapsed : Visibility.Visible;
        LinksPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A person reuses the title layout: photo for the poster, department and life dates for the
    /// meta line, birthplace under it, the biography as the overview, and a "Known for" row.
    /// </summary>
    private void ShowPerson(TmdbPerson person)
    {
        _person = person;
        string name = person.Name ?? string.Empty;

        DetailHeaderText.Text = name;
        DetailTitle.Text = name;
        DetailMeta.Text = TmdbFormat.PersonMeta(person, DateOnly.FromDateTime(DateTime.Today), CultureInfo.CurrentCulture);
        DetailRatingPanel.Visibility = Visibility.Collapsed;
        SetText(DetailGenres, person.PlaceOfBirth);
        SetText(DetailTagline, null);
        SetText(DetailOverview, string.IsNullOrWhiteSpace(person.Biography) ? "No biography yet." : person.Biography);
        SetText(DetailCredit, null);
        SetText(DetailWatch, null);
        WatchExpander.Visibility = Visibility.Collapsed;
        BackdropBorder.Visibility = Visibility.Collapsed;
        SetImage(PosterImage, TmdbFormat.ImageUri(person.ProfilePath, "w185"), 92);

        List<TmdbSearchItem> knownFor = TmdbFormat.KnownFor(person, MaxKnownFor);
        KnownForRepeater.ItemsSource = knownFor;
        KnownForSection.Visibility = knownFor.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        TrailerButton.Visibility = Visibility.Collapsed;
        ImdbButton.Visibility = TmdbFormat.ImdbUri(person.ImdbId) is null ? Visibility.Collapsed : Visibility.Visible;
        LinksPanel.Visibility = ImdbButton.Visibility;
    }

    private void SetRating(double average, int count)
    {
        string rating = TmdbFormat.Rating(average, count);
        DetailRating.Text = rating;
        DetailVotes.Text = TmdbFormat.VoteCount(count);
        DetailRatingPanel.Visibility = VisibleIf(rating);
    }

    private static void SetText(TextBlock block, string? text)
    {
        block.Text = text ?? string.Empty;
        block.Visibility = VisibleIf(text);
    }

    private static void SetImage(Image image, Uri? uri, int logicalWidth) =>
        image.Source = uri is null
            ? null
            : new BitmapImage(uri) { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = logicalWidth };

    private void ShowSearchView()
    {
        _detailsCts?.Cancel();
        _history.Clear();
        _detailItem = null;
        _details = null;
        _person = null;
        DetailView.Visibility = Visibility.Collapsed;
        SearchView.Visibility = Visibility.Visible;
        CastRepeater.ItemsSource = null;
        CrewRepeater.ItemsSource = null;
        KnownForRepeater.ItemsSource = null;
        WatchExpander.IsExpanded = false;
        WatchGroupsRepeater.ItemsSource = null;
        BackdropImage.Source = null;
        PosterImage.Source = null;
        if (_showingLibrary)
        {
            ShowLibrary(); // Flags may have changed on the detail page.
        }

        FocusCurrentView();
    }

    private void FocusCurrentView()
    {
        if (IsShowingDetails)
        {
            BackButton.Focus(FocusState.Programmatic);
            return;
        }

        if (_showingLibrary)
        {
            LibraryFilterBar.Focus(FocusState.Programmatic);
            return;
        }

        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    // My stuff

    private void LibraryButton_Click(object sender, RoutedEventArgs e) => SetLibraryMode(!_showingLibrary);

    private void SetLibraryMode(bool on)
    {
        _showingLibrary = on;
        ShowModeChrome();
        if (on)
        {
            ShowLibrary();
        }
        else if (SearchBox.Text.Trim() is { Length: > 0 } query && query != _shownQuery)
        {
            _ = RunSearchAsync(query);
        }
        else if (_shownQuery is not null)
        {
            ShowSearchResults();
        }
        else
        {
            ShowTrending();
        }

        FocusCurrentView();
    }

    /// <summary>The header, and the search box or the filter bar, for the current mode.</summary>
    private void ShowModeChrome()
    {
        TitleText.Text = _showingLibrary ? "My stuff" : App.DisplayName;
        SearchBox.Visibility = _showingLibrary ? Visibility.Collapsed : Visibility.Visible;
        LibraryFilterBar.Visibility = _showingLibrary ? Visibility.Visible : Visibility.Collapsed;

        string label = _showingLibrary ? "Search" : "My stuff";
        LibraryButtonIcon.Glyph = _showingLibrary ? "\uE721" : "\uE8F1"; // Search, Library
        AutomationProperties.SetName(LibraryButton, label);
        ToolTipService.SetToolTip(LibraryButton, label);
    }

    private void ShowLibrary()
    {
        CenterRing.IsActive = false;
        List<LibraryEntry> entries = LibraryService.Library.Filter(_libraryFilter);
        ShowList(entries, entries.Count switch
        {
            0 => string.Empty,
            1 => "1 title",
            int n => $"{n} titles",
        });

        if (entries.Count == 0)
        {
            ShowMessage(_libraryFilter switch
            {
                LibraryFilter.Seen => "Nothing marked as seen yet.",
                LibraryFilter.WantToWatch => "Nothing on your want-to-watch list yet.",
                _ => "Nothing here yet. Open a movie or show and mark it Seen or Want to watch.",
            });
        }
    }

    private void LibraryFilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _libraryFilter = sender.SelectedItem?.Tag is string tag && Enum.TryParse(tag, out LibraryFilter filter)
            ? filter
            : LibraryFilter.All;
        if (_showingLibrary)
        {
            ShowLibrary();
        }
    }

    /// <summary>The title on the detail page, with the loaded details' title and poster when there are some.</summary>
    private LibraryEntry? CurrentTitle()
    {
        if (_detailItem is null || LibraryEntry.From(_detailItem) is not { } title)
        {
            return null;
        }

        if (_details is not null)
        {
            title.Title = _details.DisplayTitle;
            title.Date = _details.ReleaseDate ?? _details.FirstAirDate ?? title.Date;
            title.PosterPath = _details.PosterPath ?? title.PosterPath;
        }

        return title;
    }

    private void ShowLibraryFlags()
    {
        LibraryEntry? title = CurrentTitle();
        LibraryEntry? saved = title is null ? null : LibraryService.Library.Find(title.Kind, title.Id);
        LibraryPanel.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        SeenToggle.IsChecked = saved?.Seen == true;
        WantToggle.IsChecked = saved?.WantToWatch == true;
    }

    private void SeenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTitle() is { } title)
        {
            LibraryService.SetSeen(title, SeenToggle.IsChecked == true);
        }

        ShowLibraryFlags(); // Marking it seen clears Want to watch.
    }

    private void WantToggle_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTitle() is { } title)
        {
            LibraryService.SetWantToWatch(title, WantToggle.IsChecked == true);
        }

        ShowLibraryFlags();
    }

    // Links

    private Uri? CurrentPageUri =>
        _detailItem is { Kind: { } kind } item ? TmdbFormat.PageUri(kind, item.Id) : null;

    private void OpenTmdb_Click(object sender, RoutedEventArgs e) => Open(CurrentPageUri);

    private void TrailerButton_Click(object sender, RoutedEventArgs e) => Open(_details is null ? null : TmdbFormat.TrailerUri(_details));

    private void ShowWatchProviders()
    {
        if (_details is not { } details || WatchGroupsRepeater.ItemsSource is not null)
        {
            return;
        }

        string region = TmdbService.Region;
        List<TmdbProviderGroup> groups = TmdbFormat.WatchGroups(details, region);
        WatchRegionText.Text = string.Format(CultureInfo.CurrentCulture, s_resources.Value.GetString("WatchRegion"), region);
        WatchGroupsRepeater.ItemsSource = groups;
        WatchEmptyText.Text = string.Format(CultureInfo.CurrentCulture, s_resources.Value.GetString("WatchEmpty"), region);
        WatchEmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WatchExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args) => ShowWatchProviders();

    private void DetailScroller_AnchorRequested(ScrollViewer sender, AnchorRequestedEventArgs args)
    {
        if (!IsShowingDetails || WatchExpander.Visibility != Visibility.Visible)
        {
            return;
        }

        double headerTop = WatchExpander.TransformToVisual(sender).TransformPoint(default).Y;
        if (headerTop >= 0 && headerTop < sender.ViewportHeight)
        {
            // Anchor the unchanged top edge, not provider rows inserted below it.
            args.Anchor = WatchExpander;
        }
    }

    private void ImdbButton_Click(object sender, RoutedEventArgs e) => Open(TmdbFormat.ImdbUri(_person?.ImdbId ?? _details?.ImdbId));

    private static void Open(Uri? uri)
    {
        if (uri is not null)
        {
            _ = Launcher.LaunchUriAsync(uri);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    // Home skips the whole trail; the search box and results are kept as they were.
    private void HomeButton_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    private void Home_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HomeRequested?.Invoke();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
}
