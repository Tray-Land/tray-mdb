using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayMDB.Tmdb;

/// <summary>Which part of the library "My stuff" shows.</summary>
public enum LibraryFilter
{
    All,
    Seen,
    WantToWatch,
}

/// <summary>
/// A movie or show the user has flagged, with enough of the title stored to list it without
/// calling TMDB.
/// </summary>
public sealed class LibraryEntry
{
    public MediaKind Kind { get; set; }

    public int Id { get; set; }

    public string? Title { get; set; }

    /// <summary>Release date for a movie, first air date for a show.</summary>
    public string? Date { get; set; }

    public string? PosterPath { get; set; }

    public bool Seen { get; set; }

    public bool WantToWatch { get; set; }

    public DateTimeOffset Updated { get; set; }

    [JsonIgnore]
    public string StatusText => TmdbFormat.JoinParts(Seen ? "Seen" : null, WantToWatch ? "Want to watch" : null);

    /// <summary>"Movie · 2010" or "TV · 2008", like a search row.</summary>
    [JsonIgnore]
    public string Subtitle => TmdbFormat.JoinParts(Kind == MediaKind.Tv ? "TV" : "Movie", TmdbFormat.Year(Date));

    [JsonIgnore]
    public Uri? PosterUri => TmdbFormat.ImageUri(PosterPath, "w92");

    /// <summary>What a screen reader announces for a list row, e.g. "Inception, Movie, 2010, Seen".</summary>
    public override string ToString() =>
        TmdbFormat.JoinParts(Title, Subtitle, StatusText).Replace(" · ", ", ", StringComparison.Ordinal);

    /// <summary>The title a search row, tile, or detail page is showing; null for a person.</summary>
    public static LibraryEntry? From(TmdbSearchItem item) =>
        item.Kind is MediaKind.Movie or MediaKind.Tv
            ? new()
            {
                Kind = item.Kind.Value,
                Id = item.Id,
                Title = item.DisplayTitle,
                Date = item.ReleaseDate ?? item.FirstAirDate,
                PosterPath = item.PosterPath,
            }
            : null;

    /// <summary>A search-row stand-in, so the list template and detail page work unchanged.</summary>
    public TmdbSearchItem ToSearchItem() => Kind == MediaKind.Tv
        ? new() { Id = Id, MediaType = "tv", Name = Title, FirstAirDate = Date, PosterPath = PosterPath }
        : new() { Id = Id, MediaType = "movie", Title = Title, ReleaseDate = Date, PosterPath = PosterPath };
}

/// <summary>
/// The user's seen and want-to-watch flags. Marking a title seen takes it off the want-to-watch
/// list; a title with neither flag is dropped.
/// </summary>
public sealed class Library
{
    private readonly List<LibraryEntry> _entries;

    public Library()
        : this([])
    {
    }

    private Library(List<LibraryEntry> entries) => _entries = entries;

    public int Count => _entries.Count;

    public LibraryEntry? Find(MediaKind kind, int id) => _entries.Find(e => e.Kind == kind && e.Id == id);

    public void SetSeen(LibraryEntry title, bool seen, DateTimeOffset now) =>
        Update(title, now, e =>
        {
            e.Seen = seen;
            if (seen)
            {
                e.WantToWatch = false;
            }
        });

    public void SetWantToWatch(LibraryEntry title, bool wantToWatch, DateTimeOffset now) =>
        Update(title, now, e => e.WantToWatch = wantToWatch);

    /// <summary>Most recently flagged first.</summary>
    public List<LibraryEntry> Filter(LibraryFilter filter) =>
        [.. _entries
            .Where(e => filter switch
            {
                LibraryFilter.Seen => e.Seen,
                LibraryFilter.WantToWatch => e.WantToWatch,
                _ => true,
            })
            .OrderByDescending(e => e.Updated)];

    public string ToJson() => JsonSerializer.Serialize(_entries, LibraryJsonContext.Default.ListLibraryEntry);

    /// <summary>Reads a saved library. Anything unreadable, or entries that aren't a flagged movie or show, are dropped.</summary>
    public static Library FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new();
        }

        try
        {
            List<LibraryEntry> entries = JsonSerializer.Deserialize(json, LibraryJsonContext.Default.ListLibraryEntry) ?? [];
            return new([.. entries
                .Where(e => e is { Kind: MediaKind.Movie or MediaKind.Tv, Id: > 0 } && (e.Seen || e.WantToWatch))
                .DistinctBy(e => (e.Kind, e.Id))]);
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private void Update(LibraryEntry title, DateTimeOffset now, Action<LibraryEntry> change)
    {
        if (title.Kind is not (MediaKind.Movie or MediaKind.Tv))
        {
            throw new ArgumentException("Only movies and shows can be flagged.", nameof(title));
        }

        LibraryEntry? entry = Find(title.Kind, title.Id);
        if (entry is null)
        {
            entry = new() { Kind = title.Kind, Id = title.Id };
            _entries.Add(entry);
        }

        // Keep the stored title and poster as fresh as the page that changed the flag.
        entry.Title = title.Title ?? entry.Title;
        entry.Date = title.Date ?? entry.Date;
        entry.PosterPath = title.PosterPath ?? entry.PosterPath;
        entry.Updated = now;
        change(entry);

        if (!entry.Seen && !entry.WantToWatch)
        {
            _entries.Remove(entry);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<LibraryEntry>))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext;
