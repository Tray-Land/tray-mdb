using System.Net;
using System.Text;
using System.Text.Json;
using TrayMDB.Tmdb;

namespace TrayMDB.Tests;

public class RandomPickerTests
{
    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void Matches_MovieRuntime_EnforcesBoundaryAndRejectsUnknown(int? runtime, bool expected)
    {
        RandomFilters filters = new(MediaKind.Movie, MinMinutes: 1, MaxMinutes: 30);
        TmdbDetails details = new() { Kind = MediaKind.Movie, Id = 1, Runtime = runtime };

        Assert.Equal(expected, filters.Matches(details, new Library(), "US"));
    }

    [Fact]
    public void Matches_TvLength_UsesEpisodeNotMovieRuntime()
    {
        RandomFilters filters = new(MediaKind.Tv, MinMinutes: 31, MaxMinutes: 60);
        TmdbDetails details = new() { Kind = MediaKind.Tv, Id = 1, Runtime = 120, EpisodeRunTime = [0, 45] };

        Assert.True(filters.Matches(details, new Library(), "US"));
        details.EpisodeRunTime = [];
        Assert.False(filters.Matches(details, new Library(), "US"));
        details.LastEpisodeToAir = new() { Runtime = 45 };
        Assert.True(filters.Matches(details, new Library(), "US"));
    }

    [Fact]
    public void Matches_AnyLength_AllowsUnknownRuntime()
    {
        Assert.True(new RandomFilters(MediaKind.Movie).Matches(new() { Kind = MediaKind.Movie, Id = 1 }, new Library(), "US"));
    }

    [Theory]
    [InlineData(MediaKind.Movie, 89, false)]
    [InlineData(MediaKind.Movie, 90, true)]
    [InlineData(MediaKind.Movie, 120, true)]
    [InlineData(MediaKind.Movie, 121, false)]
    [InlineData(MediaKind.Tv, 89, false)]
    [InlineData(MediaKind.Tv, 90, true)]
    [InlineData(MediaKind.Tv, 120, true)]
    [InlineData(MediaKind.Tv, 121, false)]
    public void Matches_LengthRange_IncludesBothEndpoints(MediaKind kind, int minutes, bool expected)
    {
        RandomFilters filters = new(kind, MinMinutes: 90, MaxMinutes: 120);
        TmdbDetails details = new() { Kind = kind, Id = 1, Runtime = minutes, EpisodeRunTime = [minutes] };

        Assert.Equal(expected, filters.Matches(details, new(), "US"));
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(480, true)]
    [InlineData(null, false)]
    public void Matches_OpenEndedLength_DoesNotCapAtSliderMaximum(int? minutes, bool expected)
    {
        RandomFilters filters = new(MediaKind.Movie, MinMinutes: 300);
        TmdbDetails details = new() { Kind = MediaKind.Movie, Id = 1, Runtime = minutes };

        Assert.Equal(expected, filters.Matches(details, new(), "US"));
    }

    [Theory]
    [InlineData(MediaKind.Movie, "1989-12-31", false)]
    [InlineData(MediaKind.Movie, "1990-01-01", true)]
    [InlineData(MediaKind.Movie, "2000-12-31", true)]
    [InlineData(MediaKind.Movie, "2001-01-01", false)]
    [InlineData(MediaKind.Movie, null, false)]
    [InlineData(MediaKind.Movie, "", false)]
    [InlineData(MediaKind.Movie, "1995-02-30", false)]
    [InlineData(MediaKind.Movie, "1995", false)]
    [InlineData(MediaKind.Tv, "1989-12-31", false)]
    [InlineData(MediaKind.Tv, "1990-01-01", true)]
    [InlineData(MediaKind.Tv, "2000-12-31", true)]
    [InlineData(MediaKind.Tv, "2001-01-01", false)]
    [InlineData(MediaKind.Tv, null, false)]
    [InlineData(MediaKind.Tv, "1995-invalid", false)]
    public void Matches_YearRange_UsesCorrectDateAndInclusiveBounds(MediaKind kind, string? date, bool expected)
    {
        RandomFilters filters = new(kind, MinYear: 1990, MaxYear: 2000);
        TmdbDetails details = new()
        {
            Kind = kind,
            Id = 1,
            ReleaseDate = kind == MediaKind.Movie ? date : "1995-01-01",
            FirstAirDate = kind == MediaKind.Tv ? date : "1995-01-01",
            LastAirDate = "2025-01-01",
        };

        Assert.Equal(expected, filters.Matches(details, new(), "US"));
    }

    [Fact]
    public void Matches_AnyYear_AllowsUnknownAndPreSliderDates()
    {
        RandomFilters filters = new(MediaKind.Movie);

        Assert.True(filters.Matches(new() { Kind = MediaKind.Movie, Id = 1 }, new(), "US"));
        Assert.True(filters.Matches(new() { Kind = MediaKind.Movie, Id = 1, ReleaseDate = "1888-10-14" }, new(), "US"));
    }

    [Theory]
    [InlineData(1995, null, true)]
    [InlineData(1996, null, false)]
    [InlineData(null, 1995, true)]
    [InlineData(null, 1994, false)]
    [InlineData(1995, 1995, true)]
    public void Matches_SingleYearOrOneSidedBounds_AppliesYearConstraints(int? minYear, int? maxYear, bool expected)
    {
        RandomFilters filters = new(MediaKind.Movie, MinYear: minYear, MaxYear: maxYear);
        TmdbDetails details = new() { Kind = MediaKind.Movie, Id = 1, ReleaseDate = "1995-06-15" };

        Assert.Equal(expected, filters.Matches(details, new(), "US"));
    }

    [Theory]
    [InlineData(WatchOfferKind.Subscription, true)]
    [InlineData(WatchOfferKind.Free, true)]
    [InlineData(WatchOfferKind.Ads, true)]
    [InlineData(WatchOfferKind.Rent, false)]
    [InlineData(WatchOfferKind.Buy, false)]
    public void Matches_Provider_RequiresStreamingInSelectedRegion(WatchOfferKind offer, bool expected)
    {
        List<TmdbProvider> providers = [new() { ProviderId = 8 }];
        TmdbWatchRegion region = new()
        {
            Flatrate = offer == WatchOfferKind.Subscription ? providers : null,
            Free = offer == WatchOfferKind.Free ? providers : null,
            Ads = offer == WatchOfferKind.Ads ? providers : null,
            Rent = offer == WatchOfferKind.Rent ? providers : null,
            Buy = offer == WatchOfferKind.Buy ? providers : null,
        };
        TmdbDetails details = new() { Kind = MediaKind.Movie, Id = 1, WatchProviders = new() { Results = new() { ["US"] = region } } };
        RandomFilters filters = new(MediaKind.Movie, ProviderIds: [8]);

        Assert.Equal(expected, filters.Matches(details, new Library(), "us"));
        Assert.False(filters.Matches(details, new Library(), "GB"));
    }

    [Fact]
    public void Matches_GenreRatingAndKind_AllFiltersMustMatch()
    {
        RandomFilters filters = new(MediaKind.Movie, GenreIds: [18], MinRating: 7);
        TmdbDetails details = new() { Kind = MediaKind.Movie, Id = 1, Genres = [new() { Id = 18 }], VoteAverage = 7, VoteCount = 10 };
        Library library = new();

        Assert.True(filters.Matches(details, library, "US"));
        details.VoteAverage = 6.9;
        Assert.False(filters.Matches(details, library, "US"));
        details.VoteAverage = 7;
        details.VoteCount = 0;
        Assert.False(filters.Matches(details, library, "US"));
        details.VoteCount = 10;
        details.Genres = [];
        Assert.False(filters.Matches(details, library, "US"));
        details.Genres = [new() { Id = 18 }];
        details.Kind = MediaKind.Tv;
        Assert.False(filters.Matches(details, library, "US"));
    }

    [Fact]
    public void Matches_MultipleGenresAndProviders_MatchesAnyWithinEachCategory()
    {
        RandomFilters filters = new(MediaKind.Movie, ProviderIds: [8, 9], GenreIds: [18, 35]);
        TmdbDetails details = new()
        {
            Kind = MediaKind.Movie,
            Id = 1,
            Genres = [new() { Id = 35 }],
            WatchProviders = new() { Results = new() { ["US"] = new() { Free = [new() { ProviderId = 9 }] } } },
        };

        Assert.True(filters.Matches(details, new(), "US"));
        details.Genres = [new() { Id = 99 }];
        Assert.False(filters.Matches(details, new(), "US"));
        details.Genres = [new() { Id = 18 }];
        details.WatchProviders.Results["US"].Free = [new() { ProviderId = 10 }];
        Assert.False(filters.Matches(details, new(), "US"));
    }

    [Fact]
    public void Matches_EmptyChipCollections_DoNotRestrictResults()
    {
        RandomFilters filters = new(MediaKind.Tv, ProviderIds: [], GenreIds: []);

        filters.Validate();

        Assert.True(filters.Matches(new() { Kind = MediaKind.Tv, Id = 1 }, new(), "US"));
    }

    [Theory]
    [InlineData(WatchListMode.Any, true, true, false)]
    [InlineData(WatchListMode.Any, false, true, true)]
    [InlineData(WatchListMode.Only, true, false, true)]
    [InlineData(WatchListMode.Exclude, true, false, false)]
    public void MatchesLibrary_SeenAndWantFlags_AreRespected(WatchListMode mode, bool excludeSeen, bool seen, bool expected)
    {
        Library library = new();
        LibraryEntry entry = new() { Kind = MediaKind.Movie, Id = 1 };
        library.SetSeen(entry, seen, DateTimeOffset.UtcNow);
        library.SetWantToWatch(entry, true, DateTimeOffset.UtcNow);
        RandomFilters filters = new(MediaKind.Movie, WatchList: mode, ExcludeSeen: excludeSeen);

        Assert.Equal(expected, filters.MatchesLibrary(library, 1));
        Assert.Equal(mode != WatchListMode.Only, filters.MatchesLibrary(library, 2));
        Assert.Equal(mode != WatchListMode.Only, (filters with { Kind = MediaKind.Tv }).MatchesLibrary(library, 1));
    }

    [Fact]
    public void Validate_InvalidFilters_Throws()
    {
        RandomFilters[] invalid =
        [
            new(MediaKind.Person),
            new(MediaKind.Movie, ProviderIds: [8, 0]),
            new(MediaKind.Movie, GenreIds: [18, -1]),
            new(MediaKind.Movie, MinMinutes: 0),
            new(MediaKind.Movie, MaxMinutes: -1),
            new(MediaKind.Movie, MinMinutes: 60, MaxMinutes: 30),
            new(MediaKind.Movie, WatchList: (WatchListMode)99),
            new(MediaKind.Movie, MinRating: double.NaN),
            new(MediaKind.Movie, MinRating: 11),
            new(MediaKind.Movie, MinYear: 0),
            new(MediaKind.Movie, MaxYear: -1),
            new(MediaKind.Movie, MinYear: 10000),
            new(MediaKind.Movie, MaxYear: 10000),
            new(MediaKind.Movie, MinYear: 2020, MaxYear: 2010),
        ];

        foreach (RandomFilters filters in invalid)
        {
            Assert.Throws<ArgumentException>(filters.Validate);
        }

        new RandomFilters(MediaKind.Movie, MinMinutes: 121).Validate();
    }

    [Fact]
    public async Task PickAsync_WatchList_VisitsEveryEligibleTitleUntilMatchWithoutDiscover()
    {
        Library library = new();
        for (int id = 1; id <= 40; id++)
        {
            library.SetWantToWatch(new() { Kind = MediaKind.Movie, Id = id, Title = $"Movie {id}" }, true, DateTimeOffset.UtcNow);
        }

        library.SetWantToWatch(new() { Kind = MediaKind.Tv, Id = 99, Title = "Show" }, true, DateTimeOffset.UtcNow);
        Handler handler = new(uri =>
        {
            Assert.StartsWith("/3/movie/", uri.AbsolutePath);
            return """{"id":1,"title":"Movie","genres":[]}""";
        });
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(1));

        RandomPick? result = await picker.PickAsync(new RandomFilters(MediaKind.Movie, GenreIds: [18], WatchList: WatchListMode.Only),
            library, "en-US", "US", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(40, handler.Uris.Count);
        Assert.Equal(40, handler.Uris.Select(u => u.AbsolutePath).Distinct().Count());
    }

    [Fact]
    public async Task PickAsync_EmptyWatchList_DoesNotRequestNetwork()
    {
        Handler handler = new(_ => throw new InvalidOperationException("Unexpected request."));
        RandomPicker picker = new(new(new HttpClient(handler), "test"));

        Assert.Null(await picker.PickAsync(new(MediaKind.Tv, WatchList: WatchListMode.Only), new(), "en-US", "US", CancellationToken.None));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task PickAsync_WatchList_RejectsTitlesOutsideSelectedYears()
    {
        Library library = new();
        library.SetWantToWatch(new() { Kind = MediaKind.Movie, Id = 1 }, true, DateTimeOffset.UtcNow);
        library.SetWantToWatch(new() { Kind = MediaKind.Movie, Id = 2 }, true, DateTimeOffset.UtcNow);
        Handler handler = new(_ => """{"id":1,"title":"Older title","release_date":"1989-12-31"}""");
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(1));

        Assert.Null(await picker.PickAsync(new(MediaKind.Movie, WatchList: WatchListMode.Only, MinYear: 1990, MaxYear: 2000),
            library, "en-US", "US", CancellationToken.None));
        Assert.Equal(2, handler.Uris.Count);
        Assert.All(handler.Uris, uri => Assert.StartsWith("/3/movie/", uri.AbsolutePath));
    }

    [Fact]
    public async Task PickAsync_Catalog_ExcludesSavedAndSeenTitlesBeforeLoadingDetails()
    {
        Library library = new();
        library.SetSeen(new() { Kind = MediaKind.Movie, Id = 1 }, true, DateTimeOffset.UtcNow);
        library.SetWantToWatch(new() { Kind = MediaKind.Movie, Id = 2 }, true, DateTimeOffset.UtcNow);
        Handler handler = new(uri => uri.AbsolutePath.Contains("discover", StringComparison.Ordinal)
            ? """{"total_pages":1,"results":[{"id":1,"title":"Seen"},{"id":2,"title":"Saved"},{"id":3,"title":"Pick"}]}"""
            : """{"id":3,"title":"Pick","runtime":90,"genres":[{"id":18}]}""");
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(1));

        RandomPick? pick = await picker.PickAsync(new RandomFilters(MediaKind.Movie, GenreIds: [18], WatchList: WatchListMode.Exclude),
            library, "en-US", "US", CancellationToken.None);

        Assert.NotNull(pick);
        Assert.Equal(3, pick.Item.Id);
        Assert.Equal(MediaKind.Movie, pick.Item.Kind);
        Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("/movie/3", handler.Uris[1].AbsolutePath);
    }

    [Fact]
    public async Task PickAsync_Catalog_SamplesBeyondFirstPageAndCapsAtTmdbLimit()
    {
        Handler handler = new(_ => """{"total_pages":10000,"results":[]}""");
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(2));

        Assert.Null(await picker.PickAsync(new(MediaKind.Movie), new(), "en-US", "US", CancellationToken.None));
        Assert.InRange(handler.Uris.Count, 5, 6);
        Assert.All(handler.Uris, uri =>
        {
            string page = uri.Query.Split('&').Single(p => p.StartsWith("page=", StringComparison.Ordinal))[5..];
            Assert.InRange(int.Parse(page), 1, 500);
        });
        Assert.Contains(handler.Uris, uri => !uri.Query.Contains("page=1&", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PickAsync_Cancelled_PropagatesCancellationWithoutRequests()
    {
        Handler handler = new(_ => throw new InvalidOperationException("Unexpected request."));
        RandomPicker picker = new(new(new HttpClient(handler), "test"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            picker.PickAsync(new(MediaKind.Movie), new(), "en-US", "US", new CancellationToken(true)));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task PickAsync_ServiceFailure_PropagatesRatherThanClaimingNoMatches()
    {
        Handler handler = new(_ => """{"status_message":"Unavailable"}""", HttpStatusCode.ServiceUnavailable);
        RandomPicker picker = new(new(new HttpClient(handler), "test"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            picker.PickAsync(new(MediaKind.Movie), new(), "en-US", "US", CancellationToken.None));
    }

    [Fact]
    public async Task PickFromTypesAsync_InvalidTypeCombinations_RejectsBeforeRequesting()
    {
        Handler handler = new(_ => throw new InvalidOperationException("Unexpected request."));
        RandomPicker picker = new(new(new HttpClient(handler), "test"));
        RandomFilters[][] invalid =
        [
            [],
            [new(MediaKind.Movie), new(MediaKind.Movie)],
            [new(MediaKind.Movie), new(MediaKind.Tv, WatchList: WatchListMode.Only)],
            [new(MediaKind.Movie), new(MediaKind.Tv, GenreIds: [0])],
        ];

        foreach (RandomFilters[] filters in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                picker.PickFromTypesAsync(filters, new(), "en-US", "US", CancellationToken.None));
        }

        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task PickFromTypesAsync_MixedWatchList_DoesNotConfuseIdenticalMovieAndTvIds()
    {
        Library library = new();
        library.SetWantToWatch(new() { Kind = MediaKind.Movie, Id = 7 }, true, DateTimeOffset.UtcNow);
        library.SetSeen(new() { Kind = MediaKind.Movie, Id = 7 }, true, DateTimeOffset.UtcNow);
        library.SetWantToWatch(new() { Kind = MediaKind.Tv, Id = 7 }, true, DateTimeOffset.UtcNow);
        Handler handler = new(uri =>
        {
            Assert.Equal("/3/tv/7", uri.AbsolutePath);
            return """{"id":7,"name":"TV pick","genres":[{"id":10765}]}""";
        });
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(1));

        RandomPick? pick = await picker.PickFromTypesAsync(
            [new(MediaKind.Movie, GenreIds: [878], WatchList: WatchListMode.Only),
                new(MediaKind.Tv, GenreIds: [10765], WatchList: WatchListMode.Only)],
            library, "en-US", "US", CancellationToken.None);

        Assert.NotNull(pick);
        Assert.Equal(MediaKind.Tv, pick.Item.Kind);
        Assert.Equal(MediaKind.Tv, pick.Details.Kind);
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task PickFromTypesAsync_MixedCatalog_DeduplicatesByKindAndUsesEachKindsFilters()
    {
        Handler handler = new(uri => uri.AbsolutePath switch
        {
            "/3/discover/movie" or "/3/discover/tv" => """{"total_pages":1,"results":[{"id":7,"title":"Candidate"},{"id":7,"title":"Candidate"}]}""",
            "/3/movie/7" => """{"id":7,"title":"Movie","genres":[{"id":10765}]}""",
            "/3/tv/7" => """{"id":7,"name":"TV","genres":[{"id":878}]}""",
            _ => throw new InvalidOperationException("Unexpected request."),
        });
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(1));

        RandomPick? pick = await picker.PickFromTypesAsync(
            [new(MediaKind.Movie, GenreIds: [878]), new(MediaKind.Tv, GenreIds: [10765])],
            new(), "en-US", "US", CancellationToken.None);

        Assert.Null(pick);
        Assert.Equal(4, handler.Uris.Count);
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == "/3/movie/7");
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == "/3/tv/7");
    }

    [Fact]
    public async Task PickFromTypesAsync_MixedCatalog_CapsCombinedPageSample()
    {
        Handler handler = new(_ => """{"total_pages":10000,"results":[]}""");
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(2));

        Assert.Null(await picker.PickFromTypesAsync([new(MediaKind.Movie), new(MediaKind.Tv)],
            new(), "en-US", "US", CancellationToken.None));

        Assert.InRange(handler.Uris.Count, 6, 7);
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == "/3/discover/movie");
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == "/3/discover/tv");
        Assert.All(handler.Uris, uri =>
        {
            string page = uri.Query.Split('&').Single(p => p.StartsWith("page=", StringComparison.Ordinal))[5..];
            Assert.InRange(int.Parse(page), 1, 500);
        });
    }

    [Fact]
    public async Task PickFromTypesAsync_MixedCatalog_ChecksAtMostThirtyCandidates()
    {
        string page = JsonSerializer.Serialize(new
        {
            total_pages = 1,
            results = Enumerable.Range(1, 40).Select(id => new { id, title = "Candidate" }),
        });
        Handler handler = new(uri => uri.AbsolutePath.Contains("discover", StringComparison.Ordinal)
            ? page : """{"id":1,"genres":[]}""");
        RandomPicker picker = new(new(new HttpClient(handler), "test"), new Random(1));

        Assert.Null(await picker.PickFromTypesAsync(
            [new(MediaKind.Movie, GenreIds: [18]), new(MediaKind.Tv, GenreIds: [18])],
            new(), "en-US", "US", CancellationToken.None));

        Assert.Equal(2, handler.Uris.Count(uri => uri.AbsolutePath.Contains("discover", StringComparison.Ordinal)));
        Assert.Equal(30, handler.Uris.Count(uri => !uri.AbsolutePath.Contains("discover", StringComparison.Ordinal)));
    }

    internal sealed class Handler(Func<Uri, string> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri!;
            Uris.Add(uri);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(respond(uri), Encoding.UTF8, "application/json"),
            });
        }
    }
}
