using System.Globalization;

namespace TrayMDB.Tmdb;

public enum WatchListMode
{
    Any,
    Only,
    Exclude,
}

/// <summary>Length is a movie's runtime or a TV show's typical episode runtime, in minutes.</summary>
public sealed record RandomFilters(
    MediaKind Kind,
    IReadOnlyList<int>? ProviderIds = null,
    IReadOnlyList<int>? GenreIds = null,
    int? MinMinutes = null,
    int? MaxMinutes = null,
    WatchListMode WatchList = WatchListMode.Any,
    bool ExcludeSeen = true,
    double MinRating = 0,
    int? MinYear = null,
    int? MaxYear = null)
{
    public void Validate()
    {
        if (Kind is not (MediaKind.Movie or MediaKind.Tv) || !Enum.IsDefined(WatchList)
            || ProviderIds?.Any(id => id <= 0) == true || GenreIds?.Any(id => id <= 0) == true
            || MinMinutes is <= 0 || MaxMinutes is <= 0
            || (MinMinutes.HasValue && MaxMinutes.HasValue && MinMinutes > MaxMinutes)
            || MinYear is <= 0 or > 9999 || MaxYear is <= 0 or > 9999
            || (MinYear.HasValue && MaxYear.HasValue && MinYear > MaxYear)
            || !double.IsFinite(MinRating) || MinRating is < 0 or > 10)
        {
            throw new ArgumentException("Invalid random selection filters.");
        }
    }

    public bool MatchesLibrary(Library library, int id)
    {
        LibraryEntry? entry = library.Find(Kind, id);
        return !(ExcludeSeen && entry?.Seen == true) && WatchList switch
        {
            WatchListMode.Only => entry?.WantToWatch == true,
            WatchListMode.Exclude => entry?.WantToWatch != true,
            _ => true,
        };
    }

    public bool Matches(TmdbDetails details, Library library, string region)
    {
        if (details.Kind != Kind || !MatchesLibrary(library, details.Id)
            || (GenreIds is { Count: > 0 } && !details.Genres.Any(g => GenreIds.Contains(g.Id)))
            || (MinRating > 0 && (details.VoteCount <= 0 || details.VoteAverage < MinRating)))
        {
            return false;
        }

        if (MinYear.HasValue || MaxYear.HasValue)
        {
            string? date = Kind == MediaKind.Movie ? details.ReleaseDate : details.FirstAirDate;
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly released)
                || released.Year < MinYear || released.Year > MaxYear)
            {
                return false;
            }
        }

        if (MinMinutes.HasValue || MaxMinutes.HasValue)
        {
            int? minutes = Kind == MediaKind.Movie
                ? details.Runtime
                : details.EpisodeRunTime?.FirstOrDefault(m => m > 0);
            if (Kind == MediaKind.Tv && minutes is null or <= 0)
            {
                minutes = details.LastEpisodeToAir?.Runtime;
            }

            if (minutes is null or <= 0 || minutes < MinMinutes || minutes > MaxMinutes)
            {
                return false;
            }
        }

        if (ProviderIds is { Count: > 0 })
        {
            if (details.WatchProviders?.Results.TryGetValue(region.ToUpperInvariant(), out TmdbWatchRegion? offers) != true || offers is null)
            {
                return false;
            }

            return (offers.Flatrate ?? []).Concat(offers.Free ?? []).Concat(offers.Ads ?? [])
                .Any(p => ProviderIds.Contains(p.ProviderId));
        }

        return true;
    }
}

public sealed record RandomPick(TmdbSearchItem Item, TmdbDetails Details);

/// <summary>Shuffles the entire watchlist, or samples up to five TMDB pages and verifies up to 30 candidates.</summary>
public sealed class RandomPicker(TmdbClient client, Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;

    public Task<RandomPick?> PickAsync(RandomFilters filters, Library library, string language, string region, CancellationToken cancellationToken) =>
        PickFromTypesAsync([filters], library, language, region, cancellationToken);

    public async Task<RandomPick?> PickFromTypesAsync(IReadOnlyList<RandomFilters> filters, Library library, string language, string region, CancellationToken cancellationToken)
    {
        if (filters.Count == 0 || filters.Select(f => f.Kind).Distinct().Count() != filters.Count
            || filters.Select(f => f.WatchList).Distinct().Count() != 1)
        {
            throw new ArgumentException("Select distinct content types with the same watchlist mode.", nameof(filters));
        }

        foreach (RandomFilters filter in filters)
        {
            filter.Validate();
        }

        Dictionary<MediaKind, RandomFilters> byKind = filters.ToDictionary(f => f.Kind);
        cancellationToken.ThrowIfCancellationRequested();
        if (filters[0].WatchList == WatchListMode.Only)
        {
            TmdbSearchItem[] saved = library.Filter(LibraryFilter.WantToWatch)
                .Where(e => byKind.TryGetValue(e.Kind, out RandomFilters? filter) && filter.MatchesLibrary(library, e.Id))
                .Select(e => e.ToSearchItem()).ToArray();
            _random.Shuffle(saved);
            return await FindMatchAsync(saved, byKind, library, language, region, cancellationToken);
        }

        List<(RandomFilters Filter, int Number, TmdbPage<TmdbSearchItem> First)> availablePages = [];
        foreach (RandomFilters filter in filters)
        {
            TmdbPage<TmdbSearchItem> first = await client.DiscoverAsync(filter, language, region, 1, cancellationToken);
            availablePages.AddRange(Enumerable.Range(1, Math.Clamp(first.TotalPages, 1, 500))
                .Select(number => (filter, number, first)));
        }

        var shuffledPages = availablePages.ToArray();
        _random.Shuffle(shuffledPages);
        List<TmdbSearchItem> candidates = [];
        foreach (var pageInfo in shuffledPages.Take(5))
        {
            TmdbPage<TmdbSearchItem> page = pageInfo.Number == 1 ? pageInfo.First
                : await client.DiscoverAsync(pageInfo.Filter, language, region, pageInfo.Number, cancellationToken);
            candidates.AddRange(page.Results.Where(i => pageInfo.Filter.MatchesLibrary(library, i.Id)));
        }

        TmdbSearchItem[] shuffled = candidates.DistinctBy(i => (i.Kind, i.Id)).ToArray();
        _random.Shuffle(shuffled);
        return await FindMatchAsync(shuffled.Take(30), byKind, library, language, region, cancellationToken);
    }

    private async Task<RandomPick?> FindMatchAsync(IEnumerable<TmdbSearchItem> candidates, IReadOnlyDictionary<MediaKind, RandomFilters> filters, Library library, string language, string region, CancellationToken cancellationToken)
    {
        foreach (TmdbSearchItem item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RandomFilters filter = filters[item.Kind ?? throw new InvalidOperationException("A random candidate has no content type.")];
            TmdbDetails details = await client.GetDetailsAsync(filter.Kind, item.Id, language, cancellationToken);
            if (filter.Matches(details, library, region))
            {
                return new(item, details);
            }
        }

        return null;
    }
}
