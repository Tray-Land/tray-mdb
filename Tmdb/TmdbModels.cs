using System.Text.Json.Serialization;

namespace TrayMDB.Tmdb;

/// <summary>What a search row or detail page is about. Collections and companies are skipped.</summary>
public enum MediaKind
{
    Movie,
    Tv,
    Person,
}

public sealed class TmdbPage<T>
{
    public List<T> Results { get; set; } = [];

    public int TotalResults { get; set; }
}

/// <summary>
/// A movie, show, or person as TMDB lists them: a row from <c>/search/multi</c> or
/// <c>/trending/all/day</c>, a person's <c>known_for</c> title, or one of their combined credits.
/// </summary>
public sealed class TmdbSearchItem
{
    public int Id { get; set; }

    public string? MediaType { get; set; }

    public string? Title { get; set; }

    public string? Name { get; set; }

    public string? ReleaseDate { get; set; }

    public string? FirstAirDate { get; set; }

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }

    public double VoteAverage { get; set; }

    public int VoteCount { get; set; }

    public List<int>? GenreIds { get; set; }

    // Person rows
    public string? ProfilePath { get; set; }

    public string? KnownForDepartment { get; set; }

    public List<TmdbSearchItem>? KnownFor { get; set; }

    // Combined-credit rows
    public string? Character { get; set; }

    public string? Job { get; set; }

    public string? Department { get; set; }

    [JsonIgnore]
    public MediaKind? Kind => TmdbFormat.ParseKind(MediaType);

    [JsonIgnore]
    public string DisplayTitle => Title ?? Name ?? string.Empty;

    [JsonIgnore]
    public string Subtitle => Kind switch
    {
        MediaKind.Person => TmdbFormat.JoinParts(
            TmdbFormat.DepartmentLabel(KnownForDepartment),
            string.Join(", ", KnownFor?.Select(k => k.DisplayTitle).Where(t => t.Length > 0).Take(2) ?? [])),
        MediaKind.Tv => TmdbFormat.JoinParts("TV", TmdbFormat.Year(FirstAirDate)),
        _ => TmdbFormat.JoinParts("Movie", TmdbFormat.Year(ReleaseDate)),
    };

    /// <summary>The year, for a "Known for" tile.</summary>
    [JsonIgnore]
    public string YearText => TmdbFormat.Year(ReleaseDate ?? FirstAirDate);

    /// <summary>The character played or the job done, for a "Known for" tile.</summary>
    [JsonIgnore]
    public string RoleText => string.IsNullOrWhiteSpace(Character) ? Job ?? string.Empty : Character;

    [JsonIgnore]
    public string RatingText => TmdbFormat.Rating(VoteAverage, VoteCount);

    [JsonIgnore]
    public Uri? PosterUri => TmdbFormat.ImageUri(PosterPath ?? ProfilePath, "w92");

    [JsonIgnore]
    public Uri? TileUri => TmdbFormat.ImageUri(PosterPath ?? ProfilePath, "w185");

    [JsonIgnore]
    public bool IsPerson => Kind == MediaKind.Person;

    /// <summary>What a screen reader announces for a list row, e.g. "Inception, Movie · 2010".</summary>
    public override string ToString() => TmdbFormat.JoinParts(DisplayTitle, Subtitle).Replace(" · ", ", ", StringComparison.Ordinal);
}

public sealed class TmdbNamed
{
    public int Id { get; set; }

    public string? Name { get; set; }

    /// <summary>Set on a show's <c>created_by</c> people; null for genres and networks.</summary>
    public string? ProfilePath { get; set; }
}

public sealed class TmdbCredits
{
    public List<TmdbCastMember> Cast { get; set; } = [];

    public List<TmdbCrewMember> Crew { get; set; } = [];
}

public sealed class TmdbCastMember
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public string? Character { get; set; }

    public string? ProfilePath { get; set; }

    [JsonIgnore]
    public Uri? ProfileUri => TmdbFormat.ImageUri(ProfilePath, "w185");

    /// <summary>A search-row stand-in, so a cast photo can open the person's page.</summary>
    public TmdbSearchItem ToPersonItem() => new() { Id = Id, MediaType = "person", Name = Name, ProfilePath = ProfilePath };
}

public sealed class TmdbCrewMember
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public string? Job { get; set; }

    public string? ProfilePath { get; set; }
}

public sealed class TmdbVideo
{
    public string? Key { get; set; }

    public string? Site { get; set; }

    public string? Type { get; set; }

    public bool Official { get; set; }
}

public sealed class TmdbReleaseDateCountry
{
    [JsonPropertyName("iso_3166_1")]
    public string? Country { get; set; }

    public List<TmdbReleaseDate> ReleaseDates { get; set; } = [];
}

public sealed class TmdbReleaseDate
{
    public string? Certification { get; set; }

    public int Type { get; set; }
}

public sealed class TmdbContentRating
{
    [JsonPropertyName("iso_3166_1")]
    public string? Country { get; set; }

    public string? Rating { get; set; }
}

public sealed class TmdbExternalIds
{
    public string? ImdbId { get; set; }
}

public sealed class TmdbWatchRegion
{
    public string? Link { get; set; }

    public List<TmdbProvider>? Flatrate { get; set; }

    public List<TmdbProvider>? Free { get; set; }

    public List<TmdbProvider>? Ads { get; set; }

    public List<TmdbProvider>? Rent { get; set; }

    public List<TmdbProvider>? Buy { get; set; }
}

public sealed class TmdbProvider
{
    public int ProviderId { get; set; }

    public string? ProviderName { get; set; }

    public int DisplayPriority { get; set; }

    public string? LogoPath { get; set; }

    [JsonIgnore]
    public Uri? LogoUri => TmdbFormat.ImageUri(LogoPath, "w92");
}

public enum WatchOfferKind
{
    Subscription,
    Free,
    Ads,
    Rent,
    Buy,
}

/// <summary>One availability category, with providers in TMDB's display order.</summary>
public sealed record TmdbProviderGroup(WatchOfferKind Kind, List<TmdbProvider> Providers);

public sealed class TmdbResults<T>
{
    public List<T> Results { get; set; } = [];
}

public sealed class TmdbWatchProviders
{
    public Dictionary<string, TmdbWatchRegion> Results { get; set; } = [];
}

/// <summary>
/// <c>/movie/{id}</c> or <c>/tv/{id}</c> with credits, videos, ratings, external IDs, and watch
/// providers appended. The two shapes share most fields; the rest are simply null for the other kind.
/// </summary>
public sealed class TmdbDetails
{
    public int Id { get; set; }

    [JsonIgnore]
    public MediaKind Kind { get; set; }

    // Movie
    public string? Title { get; set; }

    public string? ReleaseDate { get; set; }

    public int? Runtime { get; set; }

    public string? ImdbId { get; set; }

    public TmdbResults<TmdbReleaseDateCountry>? ReleaseDates { get; set; }

    // TV
    public string? Name { get; set; }

    public string? FirstAirDate { get; set; }

    public string? LastAirDate { get; set; }

    public bool InProduction { get; set; }

    public int? NumberOfSeasons { get; set; }

    public int? NumberOfEpisodes { get; set; }

    public List<int>? EpisodeRunTime { get; set; }

    public List<TmdbNamed>? CreatedBy { get; set; }

    public List<TmdbNamed>? Networks { get; set; }

    public TmdbResults<TmdbContentRating>? ContentRatings { get; set; }

    public TmdbExternalIds? ExternalIds { get; set; }

    // Shared
    public string? Tagline { get; set; }

    public string? Overview { get; set; }

    public string? Status { get; set; }

    public string? Homepage { get; set; }

    public List<TmdbNamed> Genres { get; set; } = [];

    public double VoteAverage { get; set; }

    public int VoteCount { get; set; }

    public string? PosterPath { get; set; }

    public string? BackdropPath { get; set; }

    public TmdbCredits? Credits { get; set; }

    public TmdbResults<TmdbVideo>? Videos { get; set; }

    [JsonPropertyName("watch/providers")]
    public TmdbWatchProviders? WatchProviders { get; set; }

    [JsonIgnore]
    public string DisplayTitle => Title ?? Name ?? string.Empty;
}

public sealed class TmdbCombinedCredits
{
    public List<TmdbSearchItem> Cast { get; set; } = [];

    public List<TmdbSearchItem> Crew { get; set; } = [];
}

/// <summary><c>/person/{id}</c> with combined credits and external IDs appended.</summary>
public sealed class TmdbPerson
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public string? Biography { get; set; }

    public string? Birthday { get; set; }

    public string? Deathday { get; set; }

    public string? PlaceOfBirth { get; set; }

    public string? KnownForDepartment { get; set; }

    public string? ProfilePath { get; set; }

    public string? ImdbId { get; set; }

    public TmdbCombinedCredits? CombinedCredits { get; set; }

    public TmdbExternalIds? ExternalIds { get; set; }
}

public sealed class TmdbError
{
    public int StatusCode { get; set; }

    public string? StatusMessage { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TmdbPage<TmdbSearchItem>))]
[JsonSerializable(typeof(TmdbDetails))]
[JsonSerializable(typeof(TmdbPerson))]
[JsonSerializable(typeof(TmdbError))]
internal sealed partial class TmdbJsonContext : JsonSerializerContext;
