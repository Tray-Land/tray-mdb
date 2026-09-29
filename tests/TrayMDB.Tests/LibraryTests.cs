using TrayMDB.Tmdb;

namespace TrayMDB.Tests;

public class LibraryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static LibraryEntry Movie(int id, string title = "Inception") =>
        new() { Kind = MediaKind.Movie, Id = id, Title = title, Date = "2010-07-15", PosterPath = "/p.jpg" };

    [Fact]
    public void SetSeen_NewTitle_AddsItAsSeen()
    {
        Library library = new();

        library.SetSeen(Movie(1), true, T0);

        LibraryEntry entry = Assert.Single(library.Filter(LibraryFilter.All));
        Assert.True(entry.Seen);
        Assert.False(entry.WantToWatch);
        Assert.Equal("Inception", entry.Title);
    }

    [Fact]
    public void SetSeen_OnWantToWatch_ClearsWantToWatch()
    {
        Library library = new();
        library.SetWantToWatch(Movie(1), true, T0);

        library.SetSeen(Movie(1), true, T0.AddMinutes(1));

        LibraryEntry entry = Assert.Single(library.Filter(LibraryFilter.All));
        Assert.True(entry.Seen);
        Assert.False(entry.WantToWatch);
        Assert.Empty(library.Filter(LibraryFilter.WantToWatch));
    }

    [Fact]
    public void SetWantToWatch_OnSeen_KeepsSeenForARewatch()
    {
        Library library = new();
        library.SetSeen(Movie(1), true, T0);

        library.SetWantToWatch(Movie(1), true, T0.AddMinutes(1));

        LibraryEntry entry = Assert.Single(library.Filter(LibraryFilter.All));
        Assert.True(entry.Seen);
        Assert.True(entry.WantToWatch);
        Assert.Equal("Seen · Want to watch", entry.StatusText);
    }

    [Fact]
    public void ClearingTheLastFlag_RemovesTheTitle()
    {
        Library library = new();
        library.SetSeen(Movie(1), true, T0);

        library.SetSeen(Movie(1), false, T0.AddMinutes(1));

        Assert.Equal(0, library.Count);
        Assert.Null(library.Find(MediaKind.Movie, 1));
    }

    [Fact]
    public void Filter_SplitsByFlag_NewestFirst()
    {
        Library library = new();
        library.SetSeen(Movie(1, "Old"), true, T0);
        library.SetWantToWatch(Movie(2, "Wanted"), true, T0.AddMinutes(1));
        library.SetSeen(Movie(3, "New"), true, T0.AddMinutes(2));

        Assert.Equal(["New", "Wanted", "Old"], library.Filter(LibraryFilter.All).Select(e => e.Title));
        Assert.Equal(["New", "Old"], library.Filter(LibraryFilter.Seen).Select(e => e.Title));
        Assert.Equal(["Wanted"], library.Filter(LibraryFilter.WantToWatch).Select(e => e.Title));
    }

    [Fact]
    public void MovieAndShowWithTheSameId_AreDifferentTitles()
    {
        Library library = new();
        library.SetSeen(Movie(1), true, T0);
        library.SetSeen(new LibraryEntry { Kind = MediaKind.Tv, Id = 1, Title = "A Show" }, true, T0);

        Assert.Equal(2, library.Count);
    }

    [Fact]
    public void SetSeen_Person_Throws() =>
        Assert.Throws<ArgumentException>(() => new Library().SetSeen(new LibraryEntry { Kind = MediaKind.Person, Id = 1 }, true, T0));

    [Fact]
    public void From_PersonRow_IsNull() =>
        Assert.Null(LibraryEntry.From(new TmdbSearchItem { Id = 1, MediaType = "person", Name = "Someone" }));

    [Fact]
    public void From_ShowRow_KeepsTitleAndAirDate()
    {
        LibraryEntry? entry = LibraryEntry.From(new TmdbSearchItem { Id = 1396, MediaType = "tv", Name = "Breaking Bad", FirstAirDate = "2008-01-20" });

        Assert.NotNull(entry);
        Assert.Equal(MediaKind.Tv, entry.Kind);
        Assert.Equal("Breaking Bad", entry.Title);
        Assert.Equal("TV · 2008", entry.Subtitle);
    }

    [Fact]
    public void ToSearchItem_RoundTripsKindAndTitle()
    {
        TmdbSearchItem item = new LibraryEntry { Kind = MediaKind.Tv, Id = 1396, Title = "Breaking Bad", Date = "2008-01-20" }.ToSearchItem();

        Assert.Equal(MediaKind.Tv, item.Kind);
        Assert.Equal("Breaking Bad", item.DisplayTitle);
        Assert.Equal("TV · 2008", item.Subtitle);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        Library library = new();
        library.SetSeen(Movie(1), true, T0);
        library.SetWantToWatch(new LibraryEntry { Kind = MediaKind.Tv, Id = 2, Title = "A Show" }, true, T0);

        Library loaded = Library.FromJson(library.ToJson());

        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.Find(MediaKind.Movie, 1)?.Seen);
        Assert.True(loaded.Find(MediaKind.Tv, 2)?.WantToWatch);
        Assert.Equal(T0, loaded.Find(MediaKind.Movie, 1)?.Updated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    public void FromJson_Unreadable_IsEmpty(string? json) => Assert.Equal(0, Library.FromJson(json).Count);

    [Fact]
    public void FromJson_DropsPeopleUnflaggedAndDuplicates()
    {
        const string json = """
            [
              {"kind":"Movie","id":1,"title":"A","seen":true},
              {"kind":"Movie","id":1,"title":"A again","seen":true},
              {"kind":"Person","id":2,"title":"P","seen":true},
              {"kind":"Tv","id":3,"title":"Unflagged"}
            ]
            """;

        Library library = Library.FromJson(json);

        Assert.Equal("A", Assert.Single(library.Filter(LibraryFilter.All)).Title);
    }
}
