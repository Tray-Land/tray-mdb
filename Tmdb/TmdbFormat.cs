using System.Globalization;

namespace TrayMDB.Tmdb;

/// <summary>Turns TMDB fields into the short strings the flyout shows. No WinRT, so it's unit-tested.</summary>
public static class TmdbFormat
{
    public const string ImageBase = "https://image.tmdb.org/t/p/";
    public const string SiteBase = "https://www.themoviedb.org/";

    private const string Separator = " · ";

    public static MediaKind? ParseKind(string? mediaType) => mediaType switch
    {
        "movie" => MediaKind.Movie,
        "tv" => MediaKind.Tv,
        "person" => MediaKind.Person,
        _ => null,
    };

    public static string PathSegment(MediaKind kind) => kind switch
    {
        MediaKind.Tv => "tv",
        MediaKind.Person => "person",
        _ => "movie",
    };

    /// <summary>"2024" from "2024-05-17"; empty for a missing or malformed date.</summary>
    public static string Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year) && year > 1800
            ? year.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>"2h 14m", "2h", or "45m"; empty when unknown.</summary>
    public static string Runtime(int? minutes)
    {
        if (minutes is not > 0)
        {
            return string.Empty;
        }

        int h = minutes.Value / 60;
        int m = minutes.Value % 60;
        return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    /// <summary>Random length labels stay in minutes through 90, then use hours and remaining minutes.</summary>
    public static (int Hours, int Minutes) RandomLengthParts(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        return minutes <= 90 ? (0, minutes) : (minutes / 60, minutes % 60);
    }

    /// <summary>"7.8" out of 10, or empty when nobody has voted (TMDB reports 0 then).</summary>
    public static string Rating(double average, int count) =>
        count > 0 && average > 0 ? average.ToString("0.0", CultureInfo.CurrentCulture) : string.Empty;

    public static string VoteCount(int count) =>
        count switch
        {
            <= 0 => string.Empty,
            1 => "1 vote",
            _ => $"{count.ToString("N0", CultureInfo.CurrentCulture)} votes",
        };

    public static string JoinParts(params string?[] parts) =>
        string.Join(Separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static Uri? ImageUri(string? path, string size) =>
        string.IsNullOrEmpty(path) ? null : new Uri(ImageBase + size + path);

    public static Uri PageUri(MediaKind kind, int id) => new($"{SiteBase}{PathSegment(kind)}/{id}");

    /// <summary>IMDb's page for a title ("tt…") or a person ("nm…").</summary>
    public static Uri? ImdbUri(string? imdbId) =>
        string.IsNullOrWhiteSpace(imdbId) ? null
        : new Uri($"https://www.imdb.com/{(imdbId.StartsWith("nm", StringComparison.Ordinal) ? "name" : "title")}/{imdbId}/");

    /// <summary>"Actor" for TMDB's "Acting", and so on; other departments as TMDB names them.</summary>
    public static string DepartmentLabel(string? department) => department switch
    {
        null or "" => string.Empty,
        "Acting" => "Actor",
        "Directing" => "Director",
        "Writing" => "Writer",
        "Production" => "Producer",
        _ => department,
    };

    /// <summary>
    /// "Actor · Born March 3, 1970 (age 56)", or "Actor · June 1, 1926 – August 5, 1962 (aged 36)".
    /// </summary>
    public static string PersonMeta(TmdbPerson person, DateOnly today, CultureInfo culture)
    {
        string life = string.Empty;
        if (ParseDate(person.Birthday) is { } born)
        {
            string bornText = born.ToString("MMMM d, yyyy", culture);
            life = ParseDate(person.Deathday) is { } died
                ? $"{bornText} – {died.ToString("MMMM d, yyyy", culture)} (aged {Age(born, died)})"
                : $"Born {bornText} (age {Age(born, today)})";
        }

        return JoinParts(DepartmentLabel(person.KnownForDepartment), life);
    }

    public static int Age(DateOnly born, DateOnly on)
    {
        int age = on.Year - born.Year;
        return on < born.AddYears(age) ? age - 1 : age;
    }

    /// <summary>
    /// The titles a person is best known for, most-voted first: their acting roles for an actor,
    /// or their work in their own department (directing, writing, ...) otherwise. Talk shows, news,
    /// and appearances as themselves are left out, and each title appears once.
    /// </summary>
    public static List<TmdbSearchItem> KnownFor(TmdbPerson person, int max)
    {
        List<TmdbSearchItem> cast = person.CombinedCredits?.Cast ?? [];
        List<TmdbSearchItem> crew = person.CombinedCredits?.Crew ?? [];
        string department = person.KnownForDepartment ?? "Acting";

        IEnumerable<TmdbSearchItem> credits = department == "Acting"
            ? cast
            : crew.Where(c => c.Department == department);
        List<TmdbSearchItem> picked = Rank(credits, max);
        return picked.Count > 0 ? picked : Rank(cast.Concat(crew), max);
    }

    private static List<TmdbSearchItem> Rank(IEnumerable<TmdbSearchItem> credits, int max) =>
        credits
            .Where(c => c.Kind is MediaKind.Movie or MediaKind.Tv && !IsAppearance(c))
            .GroupBy(c => (c.Kind, c.Id))
            .Select(g => g.First())
            .OrderByDescending(c => c.VoteCount)
            .Take(max)
            .ToList();

    // TMDB genre IDs: 10763 News, 10767 Talk.
    private static bool IsAppearance(TmdbSearchItem credit) =>
        credit.GenreIds?.Any(g => g is 10763 or 10767) == true
        || (credit.Character is { } character
            && (character.Contains("Self", StringComparison.OrdinalIgnoreCase)
                || character.Contains("Himself", StringComparison.OrdinalIgnoreCase)
                || character.Contains("Herself", StringComparison.OrdinalIgnoreCase)
                || character.Contains("Themselves", StringComparison.OrdinalIgnoreCase)));

    private static DateOnly? ParseDate(string? date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : null;

    /// <summary>"2019–2023" for an ended show, "2019–" while it's still running, "2019" for one year.</summary>
    public static string AirYears(string? first, string? last, bool inProduction)
    {
        string from = Year(first);
        if (from.Length == 0)
        {
            return string.Empty;
        }

        if (inProduction)
        {
            return from + "–";
        }

        string to = Year(last);
        return to.Length == 0 || to == from ? from : $"{from}–{to}";
    }

    public static string Seasons(int? count) => count switch
    {
        null or <= 0 => string.Empty,
        1 => "1 season",
        _ => $"{count} seasons",
    };

    /// <summary>The age rating for <paramref name="region"/>, e.g. "PG-13" or "TV-MA".</summary>
    public static string Certification(TmdbDetails details, string region)
    {
        if (details.Kind == MediaKind.Tv)
        {
            return details.ContentRatings?.Results
                .FirstOrDefault(r => string.Equals(r.Country, region, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Rating))
                ?.Rating ?? string.Empty;
        }

        // Prefer the theatrical rating (type 3), then any rated release in the region.
        List<TmdbReleaseDate> dates = details.ReleaseDates?.Results
            .FirstOrDefault(r => string.Equals(r.Country, region, StringComparison.OrdinalIgnoreCase))
            ?.ReleaseDates ?? [];
        return (dates.FirstOrDefault(d => d.Type == 3 && !string.IsNullOrWhiteSpace(d.Certification))
            ?? dates.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Certification)))
            ?.Certification ?? string.Empty;
    }

    /// <summary>"Movie · 2024 · PG-13 · 2h 14m" or "TV · 2019–2023 · TV-MA · 3 seasons".</summary>
    public static string MetaLine(TmdbDetails details, string region)
    {
        string certification = Certification(details, region);
        if (details.Kind == MediaKind.Tv)
        {
            return JoinParts(
                "TV",
                AirYears(details.FirstAirDate, details.LastAirDate, details.InProduction),
                certification,
                Seasons(details.NumberOfSeasons));
        }

        return JoinParts("Movie", Year(details.ReleaseDate), certification, Runtime(details.Runtime));
    }

    /// <summary>"Directed by X" for a movie, "Created by X and Y" for a show; empty if unknown.</summary>
    public static string Credit(TmdbDetails details)
    {
        List<string> names = details.Kind == MediaKind.Tv
            ? details.CreatedBy?.Select(c => c.Name).OfType<string>().ToList() ?? []
            : details.Credits?.Crew.Where(c => c.Job == "Director").Select(c => c.Name).OfType<string>().Distinct().ToList() ?? [];

        if (names.Count == 0)
        {
            return string.Empty;
        }

        string list = names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}",
        };
        return (details.Kind == MediaKind.Tv ? "Created by " : "Directed by ") + list;
    }

    // Jobs worth a tile, in the order they're shown.
    private static readonly string[] KeyJobs =
    [
        "Director", "Screenplay", "Writer", "Story", "Novel", "Original Music Composer", "Director of Photography",
    ];

    /// <summary>
    /// The people behind a title, one tile each with their jobs joined ("Director, Screenplay"):
    /// creators for a show, then directors, writers, composer, and cinematographer. Reuses
    /// <see cref="TmdbCastMember"/> so the cast tile template shows them, with the job as the role.
    /// </summary>
    public static List<TmdbCastMember> KeyCrew(TmdbDetails details, int max)
    {
        List<(int Id, string Name, string Job, string? Profile)> credits = [];
        if (details.Kind == MediaKind.Tv)
        {
            credits.AddRange((details.CreatedBy ?? [])
                .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => (c.Id, c.Name!, "Creator", c.ProfilePath)));
        }

        credits.AddRange((details.Credits?.Crew ?? [])
            .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name) && c.Job is not null && KeyJobs.Contains(c.Job))
            .OrderBy(c => Array.IndexOf(KeyJobs, c.Job))
            .Select(c => (c.Id, c.Name!, c.Job!, c.ProfilePath)));

        return credits
            .GroupBy(c => c.Id)
            .Select(g => new TmdbCastMember
            {
                Id = g.Key,
                Name = g.First().Name,
                Character = string.Join(", ", g.Select(c => c.Job).Distinct()),
                ProfilePath = g.Select(c => c.Profile).FirstOrDefault(p => p is not null),
            })
            .Take(max)
            .ToList();
    }

    public static string Genres(TmdbDetails details) =>
        string.Join(", ", details.Genres.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)));

    /// <summary>The best YouTube trailer: official trailers first, then any trailer, then a teaser.</summary>
    public static Uri? TrailerUri(TmdbDetails details)
    {
        List<TmdbVideo> youTube = details.Videos?.Results
            .Where(v => v.Site == "YouTube" && !string.IsNullOrWhiteSpace(v.Key))
            .ToList() ?? [];
        TmdbVideo? best = youTube.FirstOrDefault(v => v.Type == "Trailer" && v.Official)
            ?? youTube.FirstOrDefault(v => v.Type == "Trailer")
            ?? youTube.FirstOrDefault(v => v.Type == "Teaser");
        return best is null ? null : new Uri($"https://www.youtube.com/watch?v={Uri.EscapeDataString(best.Key!)}");
    }

    /// <summary>
    /// Where to watch in <paramref name="region"/>: "Stream on Netflix, Max", falling back to free,
    /// then rent or buy. Empty when TMDB (via JustWatch) has nothing for the region.
    /// </summary>
    public static string WatchSummary(TmdbDetails details, string region)
    {
        if (details.WatchProviders?.Results is not { } regions
            || !regions.TryGetValue(region.ToUpperInvariant(), out TmdbWatchRegion? watch))
        {
            return string.Empty;
        }

        (string Label, List<TmdbProvider>? Providers)[] tiers =
        [
            ("Stream on", watch.Flatrate),
            ("Free on", watch.Free ?? watch.Ads),
            ("Rent or buy on", Union(watch.Rent, watch.Buy)),
        ];

        foreach ((string label, List<TmdbProvider>? providers) in tiers)
        {
            List<string> names = providers?
                .OrderBy(p => p.DisplayPriority)
                .Select(p => p.ProviderName)
                .OfType<string>()
                .Distinct()
                .Take(4)
                .ToList() ?? [];
            if (names.Count > 0)
            {
                return $"{label} {string.Join(", ", names)}";
            }
        }

        return string.Empty;
    }

    /// <summary>All watch options in the requested region, without mixing rental and purchase offers.</summary>
    public static List<TmdbProviderGroup> WatchGroups(TmdbDetails details, string region)
    {
        if (details.WatchProviders?.Results is not { } regions
            || !regions.TryGetValue(region.ToUpperInvariant(), out TmdbWatchRegion? watch))
        {
            return [];
        }

        (WatchOfferKind Kind, List<TmdbProvider>? Providers)[] categories =
        [
            (WatchOfferKind.Subscription, watch.Flatrate),
            (WatchOfferKind.Free, watch.Free),
            (WatchOfferKind.Ads, watch.Ads),
            (WatchOfferKind.Rent, watch.Rent),
            (WatchOfferKind.Buy, watch.Buy),
        ];

        List<TmdbProviderGroup> groups = [];
        foreach ((WatchOfferKind kind, List<TmdbProvider>? providers) in categories)
        {
            List<TmdbProvider> ordered = providers?
                .Where(p => !string.IsNullOrWhiteSpace(p.ProviderName))
                .OrderBy(p => p.DisplayPriority)
                .DistinctBy(p => p.ProviderId > 0 ? $"id:{p.ProviderId}" : $"name:{p.ProviderName!.Trim()}", StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            if (ordered.Count > 0)
            {
                groups.Add(new(kind, ordered));
            }
        }

        return groups;
    }

    private static List<TmdbProvider>? Union(List<TmdbProvider>? a, List<TmdbProvider>? b) =>
        a is null ? b : b is null ? a : [.. a, .. b];
}
