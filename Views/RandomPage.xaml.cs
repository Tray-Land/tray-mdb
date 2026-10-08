using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using TrayMDB.Services;
using TrayMDB.Tmdb;

namespace TrayMDB.Views;

public sealed partial class RandomPage : Page, IDisposable
{
    private readonly Action _goBack;
    private readonly Action<RandomPick> _openDetails;
    private readonly ResourceLoader _resources = new();
    private readonly Dictionary<MediaKind, List<TmdbNamed>> _genres = [];
    private readonly Dictionary<MediaKind, List<TmdbProvider>> _providers = [];
    private readonly ObservableCollection<string> _selectedGenres = [];
    private readonly ObservableCollection<string> _selectedProviders = [];
    private CancellationTokenSource? _cts;
    private bool _optionsLoaded;
    private RandomPick? _pick;
    private bool _visible;
    private bool _ready;
    private Thumb? _lengthMinimumThumb;
    private Thumb? _lengthMaximumThumb;
    private TextBlock? _lengthToolTipText;
    private Thumb? _yearMinimumThumb;
    private Thumb? _yearMaximumThumb;

    public RandomPage(Action goBack, Action<RandomPick> openDetails)
    {
        InitializeComponent();
        _goBack = goBack;
        _openDetails = openDetails;
        RegionText.Text = string.Format(CultureInfo.CurrentCulture, Text("RandomRegion"), TmdbService.Region);
        GenreBox.ItemsSource = _selectedGenres;
        ProviderBox.ItemsSource = _selectedProviders;
        // ValueChanged only fires on drag completion; property callbacks also cover keyboard input.
        LengthRange.RegisterPropertyChangedCallback(RangeSelector.RangeStartProperty, Length_Changed);
        LengthRange.RegisterPropertyChangedCallback(RangeSelector.RangeEndProperty, Length_Changed);
        LengthMinimumText.Text = FormatLength((int)LengthRange.Minimum);
        LengthMaximumText.Text = string.Format(CultureInfo.CurrentCulture, Text("RandomLengthAtLeast"), FormatLength((int)LengthRange.Maximum));
        YearRange.Maximum = DateTime.Today.Year;
        YearRange.RangeEnd = YearRange.Maximum;
        YearRange.RegisterPropertyChangedCallback(RangeSelector.RangeStartProperty, Year_Changed);
        YearRange.RegisterPropertyChangedCallback(RangeSelector.RangeEndProperty, Year_Changed);
        YearMinimumText.Text = YearRange.Minimum.ToString("0", CultureInfo.CurrentCulture);
        YearMaximumText.Text = YearRange.Maximum.ToString("0", CultureInfo.CurrentCulture);
        _ready = true;
        UpdateLengthText();
        UpdateYearText();
    }

    private IEnumerable<MediaKind> SelectedKinds =>
        new[] { MediaKind.Movie, MediaKind.Tv }.Where(kind =>
            kind == MediaKind.Movie ? MovieToggle.IsChecked == true : TvToggle.IsChecked == true);

    public void OnShown()
    {
        _visible = true;
        if (!_optionsLoaded)
        {
            _ = LoadOptionsAsync();
        }

        BackButton.Focus(FocusState.Programmatic);
    }

    public void OnHidden()
    {
        _visible = false;
        _cts?.Cancel();
    }

    public void Dispose() => OnHidden();

    private string Text(string key) => _resources.GetString(key);

    private void SetBusy(bool busy)
    {
        foreach (Control control in new Control[] { MovieToggle, TvToggle, GenreBox, ProviderBox, LengthRange, YearRange, WatchListBox, RatingBox, ExcludeSeenBox })
        {
            control.IsEnabled = !busy;
        }
        PickButton.IsEnabled = !busy;
        LoadingRing.IsActive = busy;
    }

    private void ShowStatus(string key, InfoBarSeverity severity)
    {
        StatusBar.Message = Text(key);
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private TmdbClient? GetClient()
    {
        TmdbClient? client = TmdbService.CreateClient();
        if (client is null)
        {
            ShowStatus("RandomMissingKey", InfoBarSeverity.Error);
        }

        return client;
    }

    private void ShowError(Exception error)
    {
        // Do not log request URLs or exception text: v3 credentials live in the query string.
        Debug.WriteLine($"Random selection failed: {error.GetType().Name}");
        ShowStatus(error is TmdbAuthException ? "RandomAuthError" : "RandomNetworkError", InfoBarSeverity.Error);
    }

    private async Task LoadOptionsAsync()
    {
        if (!_visible || GetClient() is not { } client)
        {
            return;
        }

        _cts?.Cancel();
        using CancellationTokenSource cts = new();
        _cts = cts;
        SetBusy(true);
        StatusBar.IsOpen = false;
        try
        {
            foreach (MediaKind kind in new[] { MediaKind.Movie, MediaKind.Tv })
            {
                Task<List<TmdbNamed>> genres = client.GetGenresAsync(kind, TmdbService.Language, cts.Token);
                Task<List<TmdbProvider>> providers = client.GetStreamingProvidersAsync(kind, TmdbService.Language, TmdbService.Region, cts.Token);
                await Task.WhenAll(genres, providers);
                cts.Token.ThrowIfCancellationRequested();
                _genres[kind] = await genres;
                _providers[kind] = await providers;
            }

            _optionsLoaded = true;
            RefreshSuggestions();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or TmdbAuthException)
        {
            ShowError(ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                SetBusy(false);
            }
        }
    }

    private List<RandomFilters> ReadFilters()
    {
        (int? Min, int? Max) length = LengthBounds();
        (int? Min, int? Max) year = YearBounds();
        List<RandomFilters> filters = [];
        foreach (MediaKind kind in SelectedKinds)
        {
            int[] providers = _providers[kind]
                .Where(p => _selectedProviders.Contains(p.ProviderName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase))
                .Select(p => p.ProviderId).Distinct().ToArray();
            int[] genres = _genres[kind]
                .Where(g => _selectedGenres.Contains(g.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase))
                .Select(g => g.Id).Distinct().ToArray();
            if ((_selectedProviders.Count > 0 && providers.Length == 0) || (_selectedGenres.Count > 0 && genres.Length == 0))
            {
                continue;
            }

            filters.Add(new(kind, providers, genres, length.Min, length.Max,
                (WatchListMode)Math.Max(0, WatchListBox.SelectedIndex),
                ExcludeSeenBox.IsChecked == true,
                RatingBox.SelectedIndex > 0 ? RatingBox.SelectedIndex + 5 : 0,
                MinYear: year.Min, MaxYear: year.Max));
        }

        return filters;
    }

    private async void PickButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_visible)
        {
            return;
        }

        if (!_optionsLoaded)
        {
            await LoadOptionsAsync();
        }

        if (!_visible || !_optionsLoaded || GetClient() is not { } client)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(GenreBox.Text) || !string.IsNullOrWhiteSpace(ProviderBox.Text))
        {
            ShowStatus("RandomUnfinishedToken", InfoBarSeverity.Informational);
            return;
        }

        List<RandomFilters> filters = ReadFilters();
        if (filters.Count == 0)
        {
            ShowStatus(SelectedKinds.Any() ? "RandomIncompatibleChips" : "RandomChooseContent", InfoBarSeverity.Informational);
            return;
        }
        _cts?.Cancel();
        using CancellationTokenSource cts = new();
        _cts = cts;
        SetBusy(true);
        StatusBar.IsOpen = false;
        ResultButton.Visibility = Visibility.Collapsed;
        _pick = null;
        try
        {
            RandomPick? pick = await new RandomPicker(client).PickFromTypesAsync(filters, LibraryService.Library,
                TmdbService.Language, TmdbService.Region, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _pick = pick;
            if (pick is null)
            {
                ShowStatus(filters[0].WatchList == WatchListMode.Only ? "RandomNoWatchListMatch" : "RandomNoSampleMatch", InfoBarSeverity.Informational);
                return;
            }

            ResultTitle.Text = pick.Details.DisplayTitle;
            ResultMeta.Text = TmdbFormat.MetaLine(pick.Details, TmdbService.Region);
            Poster.Source = pick.Item.TileUri is { } uri ? new BitmapImage(uri) { DecodePixelWidth = 64 } : null;
            AutomationProperties.SetName(ResultButton, string.Format(CultureInfo.CurrentCulture, Text("RandomResultName"), pick.Details.DisplayTitle));
            ResultButton.Visibility = Visibility.Visible;
            BodyScroller.UpdateLayout();
            BodyScroller.ChangeView(null, BodyScroller.ScrollableHeight, null, disableAnimation: true);
            ResultButton.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or TmdbAuthException)
        {
            ShowError(ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                SetBusy(false);
            }
        }
    }

    private void ContentType_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            ClearPick();
            RefreshSuggestions();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _goBack();

    private void ResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pick is { } pick)
        {
            _openDetails(pick);
        }
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ClearPick();

    private void ExcludeSeenBox_Click(object sender, RoutedEventArgs e) => ClearPick();

    private (int? Min, int? Max) LengthBounds() =>
        (LengthRange.RangeStart > LengthRange.Minimum ? (int)LengthRange.RangeStart : null,
         LengthRange.RangeEnd < LengthRange.Maximum ? (int)LengthRange.RangeEnd : null);

    private void Length_Changed(DependencyObject sender, DependencyProperty property)
    {
        if (_ready)
        {
            ClearPick();
            UpdateLengthText();
        }
    }

    private void UpdateLengthText()
    {
        (int? min, int? max) = LengthBounds();
        LengthValueText.Text = (min, max) switch
        {
            (null, null) => Text("RandomAnyLength"),
            (null, { } upper) => string.Format(CultureInfo.CurrentCulture, Text("RandomLengthUpTo"), FormatLength(upper)),
            ({ } lower, null) => string.Format(CultureInfo.CurrentCulture, Text("RandomLengthAtLeast"), FormatLength(lower)),
            ({ } lower, { } upper) when upper <= 90 => string.Format(CultureInfo.CurrentCulture, Text("RandomLengthBetweenMinutes"), lower, upper),
            ({ } lower, { } upper) => string.Format(CultureInfo.CurrentCulture, Text("RandomLengthBetween"), FormatLength(lower), FormatLength(upper)),
        };

        if (_lengthMinimumThumb is { } minimum)
        {
            AutomationProperties.SetName(minimum, min is { } lower
                ? string.Format(CultureInfo.CurrentCulture, Text("RandomLengthMinimumName"), FormatLength(lower)) : Text("RandomLengthNoMinimum"));
        }

        if (_lengthMaximumThumb is { } maximum)
        {
            AutomationProperties.SetName(maximum, max is { } upper
                ? string.Format(CultureInfo.CurrentCulture, Text("RandomLengthMaximumName"), FormatLength(upper)) : Text("RandomLengthNoMaximum"));
        }
    }

    private string FormatLength(int totalMinutes)
    {
        (int hours, int minutes) = TmdbFormat.RandomLengthParts(totalMinutes);
        string key = (hours, minutes) switch
        {
            (0, _) => "RandomLengthMinutes",
            (_, 0) => "RandomLengthHours",
            (1, _) => "RandomLengthHourMinutes",
            _ => "RandomLengthHoursMinutes",
        };
        return hours == 0
            ? string.Format(CultureInfo.CurrentCulture, Text(key), minutes)
            : string.Format(CultureInfo.CurrentCulture, Text(key), hours, minutes);
    }

    private void LengthRange_Loaded(object sender, RoutedEventArgs e)
    {
        (_lengthMinimumThumb, _lengthMaximumThumb) = InitializeRangeThumbs(LengthRange, "RandomLength");
        if (FindRangeElement<TextBlock>(LengthRange, "ToolTipText") is { } tooltip && !ReferenceEquals(_lengthToolTipText, tooltip))
        {
            _lengthToolTipText = tooltip;
            // The Toolkit writes raw minutes into its tooltip after updating the range.
            tooltip.RegisterPropertyChangedCallback(TextBlock.TextProperty, LengthToolTip_Changed);
        }
        UpdateLengthText();
    }

    private (int? Min, int? Max) YearBounds() =>
        YearRange.RangeStart == YearRange.Minimum && YearRange.RangeEnd == YearRange.Maximum
            ? (null, null) : ((int)YearRange.RangeStart, (int)YearRange.RangeEnd);

    private void Year_Changed(DependencyObject sender, DependencyProperty property)
    {
        if (_ready)
        {
            ClearPick();
            UpdateYearText();
        }
    }

    private void UpdateYearText()
    {
        (int? min, int? max) = YearBounds();
        YearValueText.Text = min is null ? Text("RandomAnyYear")
            : string.Format(CultureInfo.CurrentCulture, Text("RandomYearBetween"), min, max);
        if (_yearMinimumThumb is { } minimum)
        {
            AutomationProperties.SetName(minimum, string.Format(CultureInfo.CurrentCulture, Text("RandomYearMinimumName"), YearRange.RangeStart));
        }

        if (_yearMaximumThumb is { } maximum)
        {
            AutomationProperties.SetName(maximum, string.Format(CultureInfo.CurrentCulture, Text("RandomYearMaximumName"), YearRange.RangeEnd));
        }
    }

    private void YearRange_Loaded(object sender, RoutedEventArgs e)
    {
        (_yearMinimumThumb, _yearMaximumThumb) = InitializeRangeThumbs(YearRange, "RandomYear");
        UpdateYearText();
    }

    private static (Thumb? Min, Thumb? Max) InitializeRangeThumbs(RangeSelector range, string automationId)
    {
        Thumb? minimum = FindRangeElement<Thumb>(range, "MinThumb");
        Thumb? maximum = FindRangeElement<Thumb>(range, "MaxThumb");
        if (minimum is not null && maximum is not null)
        {
            AutomationProperties.SetAutomationId(minimum, $"{automationId}Minimum");
            AutomationProperties.SetAutomationId(maximum, $"{automationId}Maximum");
        }
        else
        {
            Debug.WriteLine($"{automationId} range template is missing its accessible thumb controls.");
        }

        return (minimum, maximum);
    }

    private void LengthToolTip_Changed(DependencyObject sender, DependencyProperty property)
    {
        if (sender is TextBlock tooltip && int.TryParse(tooltip.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int minutes))
        {
            tooltip.Text = minutes == LengthRange.Maximum
                ? string.Format(CultureInfo.CurrentCulture, Text("RandomLengthAtLeast"), FormatLength(minutes))
                : FormatLength(minutes);
        }
    }

    private static T? FindRangeElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }

            if (FindRangeElement<T>(child, name) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private void ClearPick()
    {
        if (_ready)
        {
            _pick = null;
            ResultButton.Visibility = Visibility.Collapsed;
            StatusBar.IsOpen = false;
        }
    }

    private IEnumerable<string> GenreNames() => SelectedKinds
        .SelectMany(kind => _genres.TryGetValue(kind, out List<TmdbNamed>? genres) ? genres : [])
        .Select(genre => genre.Name).OfType<string>()
        .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);

    private IEnumerable<string> ProviderNames() => SelectedKinds
        .SelectMany(kind => _providers.TryGetValue(kind, out List<TmdbProvider>? providers) ? providers : [])
        .Select(provider => provider.ProviderName).OfType<string>()
        .Distinct(StringComparer.CurrentCultureIgnoreCase);

    private void RefreshSuggestions()
    {
        if (!_ready)
        {
            return;
        }

        GenreBox.SuggestedItemsSource = GenreNames()
            .Where(name => !_selectedGenres.Contains(name, StringComparer.CurrentCultureIgnoreCase)
                && name.Contains(GenreBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
        ProviderBox.SuggestedItemsSource = ProviderNames()
            .Where(name => !_selectedProviders.Contains(name, StringComparer.CurrentCultureIgnoreCase)
                && name.Contains(ProviderBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private void TokenTextChanged(AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ClearPick();
        }

        RefreshSuggestions();
    }

    private void GenreBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => TokenTextChanged(args);

    private void ProviderBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => TokenTextChanged(args);

    private void AddToken(TokenItemAddingEventArgs args, IEnumerable<string> choices, ObservableCollection<string> selected)
    {
        string? name = choices.FirstOrDefault(name => string.Equals(name, args.TokenText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (name is null || selected.Contains(name, StringComparer.CurrentCultureIgnoreCase))
        {
            args.Cancel = true;
            ShowStatus(name is null ? "RandomInvalidToken" : "RandomDuplicateToken", InfoBarSeverity.Informational);
            return;
        }

        args.Item = name;
    }

    private void GenreBox_TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args) => AddToken(args, GenreNames(), _selectedGenres);

    private void ProviderBox_TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args) => AddToken(args, ProviderNames(), _selectedProviders);

    private void Token_Changed(TokenizingTextBox sender, object item)
    {
        ClearPick();
        RefreshSuggestions();
    }
}
