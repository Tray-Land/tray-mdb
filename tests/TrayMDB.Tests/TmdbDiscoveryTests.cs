using TrayMDB.Tmdb;

namespace TrayMDB.Tests;

public class TmdbDiscoveryTests
{
    [Fact]
    public async Task GetDetailsAsync_TvEpisodeRuntime_ReadsLatestEpisodeFallback()
    {
        RandomPickerTests.Handler handler = new(_ => """{"id":1,"name":"Show","episode_run_time":[],"last_episode_to_air":{"runtime":45}}""");
        TmdbClient client = new(new HttpClient(handler), "test");

        TmdbDetails details = await client.GetDetailsAsync(MediaKind.Tv, 1, "en-US", CancellationToken.None);

        Assert.Equal(45, details.LastEpisodeToAir?.Runtime);
        Assert.True(new RandomFilters(MediaKind.Tv, MinMinutes: 31, MaxMinutes: 60).Matches(details, new(), "US"));
    }

    [Theory]
    [InlineData(MediaKind.Movie, "movie")]
    [InlineData(MediaKind.Tv, "tv")]
    public async Task DiscoverAsync_Filters_EncodesQueryAndSetsMediaKind(MediaKind kind, string segment)
    {
        RandomPickerTests.Handler handler = new(_ => """{"total_pages":42,"total_results":840,"results":[{"id":1,"title":"Film","name":"Show"},{"id":2,"title":""}]}""");
        TmdbClient client = new(new HttpClient(handler), "test");

        TmdbPage<TmdbSearchItem> page = await client.DiscoverAsync(new(kind, ProviderIds: [8, 9], GenreIds: [18, 35], MinMinutes: 31, MaxMinutes: 60, MinRating: 7.5),
            "en-US", "US", 12, CancellationToken.None);

        Uri uri = Assert.Single(handler.Uris);
        Assert.EndsWith($"discover/{segment}", uri.AbsolutePath);
        Assert.Contains("page=12", uri.Query);
        Assert.Contains("with_watch_providers=8%7C9", uri.Query);
        Assert.Contains("watch_region=US", uri.Query);
        Assert.Contains("with_watch_monetization_types=flatrate%7Cfree%7Cads", uri.Query);
        Assert.Contains("with_genres=18%7C35", uri.Query);
        Assert.Contains("with_runtime.gte=31", uri.Query);
        Assert.Contains("with_runtime.lte=60", uri.Query);
        Assert.Contains("vote_average.gte=7.5", uri.Query);
        Assert.Contains("include_adult=false", uri.Query);
        Assert.Contains(kind == MediaKind.Movie ? "primary_release_date.lte=" : "first_air_date.lte=", uri.Query);
        Assert.Equal(kind, Assert.Single(page.Results).Kind);
        Assert.Equal(42, page.TotalPages);
    }

    [Fact]
    public async Task DiscoverAsync_AnyFilters_OmitsOptionalConstraints()
    {
        RandomPickerTests.Handler handler = new(_ => """{"results":[]}""");
        TmdbClient client = new(new HttpClient(handler), "test");

        await client.DiscoverAsync(new(MediaKind.Movie), "en-US", "US", 1, CancellationToken.None);

        string query = handler.Uris[0].Query;
        Assert.DoesNotContain("with_watch", query);
        Assert.DoesNotContain("with_genres", query);
        Assert.DoesNotContain("with_runtime", query);
        Assert.DoesNotContain("vote_average", query);
        Assert.DoesNotContain("_date.gte", query);
    }

    [Theory]
    [InlineData(MediaKind.Movie, "primary_release_date")]
    [InlineData(MediaKind.Tv, "first_air_date")]
    public async Task DiscoverAsync_YearRange_SendsFullCalendarYearBounds(MediaKind kind, string dateField)
    {
        RandomPickerTests.Handler handler = new(_ => """{"results":[]}""");
        TmdbClient client = new(new HttpClient(handler), "test");

        await client.DiscoverAsync(new(kind, MinYear: 1990, MaxYear: 2000), "en-US", "US", 1, CancellationToken.None);

        string query = Assert.Single(handler.Uris).Query;
        Assert.Contains($"{dateField}.gte=1990-01-01", query);
        Assert.Contains($"{dateField}.lte=2000-12-31", query);
        Assert.Single(query.Split('&'), parameter => parameter.StartsWith($"{dateField}.lte=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MediaKind.Movie, "primary_release_date")]
    [InlineData(MediaKind.Tv, "first_air_date")]
    public async Task DiscoverAsync_FutureYearMaximum_StillExcludesUnreleasedTitles(MediaKind kind, string dateField)
    {
        RandomPickerTests.Handler handler = new(_ => """{"results":[]}""");
        TmdbClient client = new(new HttpClient(handler), "test");

        await client.DiscoverAsync(new(kind, MaxYear: 9999), "en-US", "US", 1, CancellationToken.None);

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"{dateField}.lte={today}", Assert.Single(handler.Uris).Query);
    }

    [Fact]
    public async Task DiscoverAsync_NonGregorianCulture_KeepsIsoYearBounds()
    {
        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        RandomPickerTests.Handler handler = new(_ => """{"results":[]}""");
        TmdbClient client = new(new HttpClient(handler), "test");
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("ar-SA");

            await client.DiscoverAsync(new(MediaKind.Movie, MinYear: 1990, MaxYear: 2000), "ar-SA", "US", 1, CancellationToken.None);

            string query = Assert.Single(handler.Uris).Query;
            Assert.Contains("primary_release_date.gte=1990-01-01", query);
            Assert.Contains("primary_release_date.lte=2000-12-31", query);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task DiscoverAsync_InvalidPage_ThrowsBeforeRequest(int page)
    {
        RandomPickerTests.Handler handler = new(_ => """{"results":[]}""");
        TmdbClient client = new(new HttpClient(handler), "test");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.DiscoverAsync(new(MediaKind.Movie), "en-US", "US", page, CancellationToken.None));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task GetGenresAsync_Response_ReadsLocalizedValidGenres()
    {
        RandomPickerTests.Handler handler = new(_ => """{"genres":[{"id":18,"name":"Drama"},{"id":0,"name":"Invalid"},{"id":1,"name":""}]}""");
        TmdbClient client = new(new HttpClient(handler), "test");

        List<TmdbNamed> genres = await client.GetGenresAsync(MediaKind.Tv, "fr-FR", CancellationToken.None);

        Assert.Equal("Drama", Assert.Single(genres).Name);
        Assert.Contains("/genre/tv/list", handler.Uris[0].AbsolutePath);
        Assert.Contains("language=fr-FR", handler.Uris[0].Query);
    }

    [Fact]
    public async Task GetStreamingProvidersAsync_Response_DeduplicatesAndSortsByPriority()
    {
        RandomPickerTests.Handler handler = new(_ => """
            {"results":[{"provider_id":2,"provider_name":"Second","display_priority":2},
                        {"provider_id":8,"provider_name":"First","display_priority":1},
                        {"provider_id":8,"provider_name":"Duplicate"},{"provider_id":0,"provider_name":"Invalid"}]}
            """);
        TmdbClient client = new(new HttpClient(handler), "test");

        List<TmdbProvider> providers = await client.GetStreamingProvidersAsync(MediaKind.Movie, "en-US", "GB", CancellationToken.None);

        Assert.Equal([8, 2], providers.Select(p => p.ProviderId));
        Assert.Contains("/watch/providers/movie", handler.Uris[0].AbsolutePath);
        Assert.Contains("watch_region=GB", handler.Uris[0].Query);
    }
}
