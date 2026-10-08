using System.Globalization;
using TrayMDB.Tmdb;

namespace TrayMDB.Tests;

public class TmdbFormatTests
{
    [Theory]
    [InlineData("2024-05-17", "2024")]
    [InlineData("1999", "1999")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("abcd-01-01", "")]
    public void Year(string? date, string expected) => Assert.Equal(expected, TmdbFormat.Year(date));

    [Theory]
    [InlineData(134, "2h 14m")]
    [InlineData(120, "2h")]
    [InlineData(45, "45m")]
    [InlineData(0, "")]
    [InlineData(null, "")]
    public void Runtime(int? minutes, string expected) => Assert.Equal(expected, TmdbFormat.Runtime(minutes));

    [Theory]
    [InlineData("2019-01-01", "2023-06-01", false, "2019–2023")]
    [InlineData("2019-01-01", "2024-06-01", true, "2019–")]
    [InlineData("2019-01-01", "2019-12-01", false, "2019")]
    [InlineData(null, null, false, "")]
    public void AirYears(string? first, string? last, bool inProduction, string expected) =>
        Assert.Equal(expected, TmdbFormat.AirYears(first, last, inProduction));

    [Fact]
    public void RatingIsEmptyWithoutVotes()
    {
        Assert.Equal(string.Empty, TmdbFormat.Rating(0, 0));
        Assert.Equal(string.Empty, TmdbFormat.Rating(8.2, 0));
    }

    [Fact]
    public void JoinPartsSkipsBlanks() => Assert.Equal("Movie · 2024", TmdbFormat.JoinParts("Movie", "", null, "2024"));

    [Fact]
    public void MovieMetaPrefersTheatricalCertification()
    {
        TmdbDetails details = new()
        {
            Kind = MediaKind.Movie,
            ReleaseDate = "2010-07-16",
            Runtime = 148,
            ReleaseDates = new()
            {
                Results =
                [
                    new() { Country = "GB", ReleaseDates = [new() { Certification = "12A", Type = 3 }] },
                    new()
                    {
                        Country = "US",
                        ReleaseDates = [new() { Certification = "", Type = 1 }, new() { Certification = "NR", Type = 4 }, new() { Certification = "PG-13", Type = 3 }],
                    },
                ],
            },
        };

        Assert.Equal("Movie · 2010 · PG-13 · 2h 28m", TmdbFormat.MetaLine(details, "US"));
        Assert.Equal("Movie · 2010 · 12A · 2h 28m", TmdbFormat.MetaLine(details, "gb"));
        Assert.Equal("Movie · 2010 · 2h 28m", TmdbFormat.MetaLine(details, "FR"));
    }

    [Fact]
    public void TvMeta()
    {
        TmdbDetails details = new()
        {
            Kind = MediaKind.Tv,
            FirstAirDate = "2008-01-20",
            LastAirDate = "2013-09-29",
            NumberOfSeasons = 5,
            ContentRatings = new() { Results = [new() { Country = "US", Rating = "TV-MA" }] },
        };

        Assert.Equal("TV · 2008–2013 · TV-MA · 5 seasons", TmdbFormat.MetaLine(details, "US"));
    }

    [Fact]
    public void CreditNamesDirectorsOrCreators()
    {
        TmdbDetails movie = new()
        {
            Kind = MediaKind.Movie,
            Credits = new() { Crew = [new() { Name = "Lana", Job = "Director" }, new() { Name = "Joel", Job = "Producer" }, new() { Name = "Lilly", Job = "Director" }] },
        };
        TmdbDetails show = new() { Kind = MediaKind.Tv, CreatedBy = [new() { Name = "A" }, new() { Name = "B" }, new() { Name = "C" }] };

        Assert.Equal("Directed by Lana and Lilly", TmdbFormat.Credit(movie));
        Assert.Equal("Created by A, B, and C", TmdbFormat.Credit(show));
        Assert.Equal(string.Empty, TmdbFormat.Credit(new TmdbDetails { Kind = MediaKind.Movie }));
    }

    [Fact]
    public void TrailerPrefersOfficialYouTubeTrailer()
    {
        TmdbDetails details = new()
        {
            Videos = new()
            {
                Results =
                [
                    new() { Site = "YouTube", Type = "Teaser", Key = "teaser", Official = true },
                    new() { Site = "Vimeo", Type = "Trailer", Key = "vimeo", Official = true },
                    new() { Site = "YouTube", Type = "Trailer", Key = "fan", Official = false },
                    new() { Site = "YouTube", Type = "Trailer", Key = "official", Official = true },
                ],
            },
        };

        Assert.Equal("https://www.youtube.com/watch?v=official", TmdbFormat.TrailerUri(details)?.AbsoluteUri);
        Assert.Null(TmdbFormat.TrailerUri(new TmdbDetails()));
    }

    [Fact]
    public void WatchSummaryFallsBackFromStreamingToRentOrBuy()
    {
        TmdbDetails details = new()
        {
            WatchProviders = new()
            {
                Results = new()
                {
                    ["US"] = new() { Flatrate = [new() { ProviderName = "Max", DisplayPriority = 2 }, new() { ProviderName = "Netflix", DisplayPriority = 1 }] },
                    ["GB"] = new() { Rent = [new() { ProviderName = "Apple TV" }], Buy = [new() { ProviderName = "Apple TV" }, new() { ProviderName = "Google Play" }] },
                },
            },
        };

        Assert.Equal("Stream on Netflix, Max", TmdbFormat.WatchSummary(details, "US"));
        Assert.Equal("Rent or buy on Apple TV, Google Play", TmdbFormat.WatchSummary(details, "gb"));
        Assert.Equal(string.Empty, TmdbFormat.WatchSummary(details, "DE"));
    }

    [Fact]
    public void WatchGroups_AllCategories_KeepsEveryOfferTypeAndProvider()
    {
        TmdbDetails details = new()
        {
            WatchProviders = new()
            {
                Results = new()
                {
                    ["US"] = new()
                    {
                        Flatrate = Enumerable.Range(1, 5).Select(i => new TmdbProvider { ProviderId = i, ProviderName = $"Stream {i}" }).ToList(),
                        Free = [new() { ProviderName = "Free service" }],
                        Ads = [new() { ProviderName = "Ad service" }],
                        Rent = [new() { ProviderId = 10, ProviderName = "Store" }],
                        Buy = [new() { ProviderId = 10, ProviderName = "Store" }],
                    },
                    ["GB"] = new() { Flatrate = [new() { ProviderName = "UK service" }] },
                },
            },
        };

        List<TmdbProviderGroup> groups = TmdbFormat.WatchGroups(details, "us");

        Assert.Equal([WatchOfferKind.Subscription, WatchOfferKind.Free, WatchOfferKind.Ads, WatchOfferKind.Rent, WatchOfferKind.Buy], groups.Select(g => g.Kind));
        Assert.Equal(5, groups[0].Providers.Count);
        Assert.Equal("Store", Assert.Single(groups[3].Providers).ProviderName);
        Assert.Equal("Store", Assert.Single(groups[4].Providers).ProviderName);
        Assert.DoesNotContain(groups.SelectMany(g => g.Providers), p => p.ProviderName == "UK service");
    }

    [Fact]
    public void WatchGroups_DuplicateAndUnnamedProviders_SortsAndFiltersWithinEachCategory()
    {
        TmdbDetails details = new()
        {
            WatchProviders = new()
            {
                Results = new()
                {
                    ["US"] = new()
                    {
                        Flatrate =
                        [
                            new() { ProviderId = 2, ProviderName = "Second", DisplayPriority = 20 },
                            new() { ProviderId = 1, ProviderName = "First", DisplayPriority = 5 },
                            new() { ProviderId = 1, ProviderName = "Duplicate", DisplayPriority = 10 },
                            new() { ProviderName = "Legacy", DisplayPriority = 30 },
                            new() { ProviderName = " legacy ", DisplayPriority = 40 },
                            new() { ProviderId = 3, ProviderName = "First", DisplayPriority = 50 },
                            new() { ProviderName = " " },
                            new(),
                        ],
                        Free = [],
                        Buy = [new() { ProviderName = "" }],
                    },
                },
            },
        };

        TmdbProviderGroup group = Assert.Single(TmdbFormat.WatchGroups(details, "US"));

        Assert.Equal(WatchOfferKind.Subscription, group.Kind);
        Assert.Equal(["First", "Second", "Legacy", "First"], group.Providers.Select(p => p.ProviderName));
    }

    [Fact]
    public void WatchGroups_MissingRegionOrData_ReturnsNoGroupsWithoutAnotherCountryFallback()
    {
        TmdbDetails details = new()
        {
            WatchProviders = new()
            {
                Results = new()
                {
                    ["US"] = new() { Flatrate = [new() { ProviderName = "US only" }] },
                    ["GB"] = new() { Link = "https://www.themoviedb.org/movie/1/watch?locale=GB" },
                },
            },
        };

        Assert.Empty(TmdbFormat.WatchGroups(details, "DE"));
        Assert.Empty(TmdbFormat.WatchGroups(details, "GB"));
        Assert.Empty(TmdbFormat.WatchGroups(new TmdbDetails(), "US"));
    }

    [Theory]
    [InlineData("/provider.jpg", "https://image.tmdb.org/t/p/w92/provider.jpg")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void LogoUri_WithOrWithoutLogo_UsesSmallTmdbImage(string? path, string? expected)
    {
        TmdbProvider provider = new() { LogoPath = path };

        Assert.Equal(expected, provider.LogoUri?.AbsoluteUri);
    }

    [Fact]
    public void PersonMetaShowsAgeOrLifespan()
    {
        CultureInfo us = CultureInfo.GetCultureInfo("en-US");
        DateOnly today = new(2026, 9, 28);

        TmdbPerson living = new() { KnownForDepartment = "Acting", Birthday = "1974-11-11" };
        TmdbPerson died = new() { KnownForDepartment = "Directing", Birthday = "1899-08-13", Deathday = "1980-04-29" };
        TmdbPerson unknown = new() { KnownForDepartment = "Sound" };

        Assert.Equal("Actor · Born November 11, 1974 (age 51)", TmdbFormat.PersonMeta(living, today, us));
        Assert.Equal("Director · August 13, 1899 – April 29, 1980 (aged 80)", TmdbFormat.PersonMeta(died, today, us));
        Assert.Equal("Sound", TmdbFormat.PersonMeta(unknown, today, us));
    }

    [Fact]
    public void KnownForUsesTheirDepartmentAndSkipsAppearances()
    {
        TmdbPerson director = new()
        {
            KnownForDepartment = "Directing",
            CombinedCredits = new()
            {
                Cast =
                [
                    new() { Id = 1, MediaType = "tv", Name = "Late Show", Character = "Self", VoteCount = 9000 },
                    new() { Id = 2, MediaType = "movie", Title = "Cameo", Character = "Man in Bar", VoteCount = 50 },
                ],
                Crew =
                [
                    new() { Id = 10, MediaType = "movie", Title = "Small", Department = "Directing", Job = "Director", VoteCount = 100 },
                    new() { Id = 11, MediaType = "movie", Title = "Big", Department = "Directing", Job = "Director", VoteCount = 5000 },
                    new() { Id = 11, MediaType = "movie", Title = "Big", Department = "Writing", Job = "Screenplay", VoteCount = 5000 },
                    new() { Id = 12, MediaType = "movie", Title = "Produced", Department = "Production", Job = "Producer", VoteCount = 8000 },
                ],
            },
        };
        TmdbPerson actor = new()
        {
            KnownForDepartment = "Acting",
            CombinedCredits = new()
            {
                Cast =
                [
                    new() { Id = 1, MediaType = "tv", Name = "News Hour", GenreIds = [10763], VoteCount = 900 },
                    new() { Id = 2, MediaType = "tv", Name = "Awards", Character = "Himself - Host", VoteCount = 800 },
                    new() { Id = 3, MediaType = "movie", Title = "Hit", Character = "Hero", VoteCount = 700 },
                    new() { Id = 3, MediaType = "movie", Title = "Hit", Character = "Hero (voice)", VoteCount = 700 },
                ],
            },
        };

        Assert.Equal(["Big", "Small"], TmdbFormat.KnownFor(director, 10).Select(k => k.DisplayTitle));
        Assert.Equal("Director", TmdbFormat.KnownFor(director, 10)[0].RoleText);
        Assert.Equal(["Hit"], TmdbFormat.KnownFor(actor, 10).Select(k => k.DisplayTitle));
        Assert.Equal(["Big"], TmdbFormat.KnownFor(director, 1).Select(k => k.DisplayTitle));
    }

    [Fact]
    public void KeyCrewPutsCreatorsFirstAndMergesJobs()
    {
        TmdbDetails show = new()
        {
            Kind = MediaKind.Tv,
            CreatedBy = [new() { Id = 1, Name = "Vince", ProfilePath = "/v.jpg" }],
            Credits = new()
            {
                Crew =
                [
                    new() { Id = 3, Name = "Composer", Job = "Original Music Composer" },
                    new() { Id = 2, Name = "Writer-Director", Job = "Writer" },
                    new() { Id = 2, Name = "Writer-Director", Job = "Director", ProfilePath = "/w.jpg" },
                    new() { Id = 4, Name = "Grip", Job = "Key Grip" },
                    new() { Id = 1, Name = "Vince", Job = "Writer" },
                ],
            },
        };

        List<TmdbCastMember> crew = TmdbFormat.KeyCrew(show, 10);

        Assert.Equal(["Vince", "Writer-Director", "Composer"], crew.Select(c => c.Name));
        Assert.Equal("Creator, Writer", crew[0].Character);
        Assert.Equal("Director, Writer", crew[1].Character);
        Assert.Equal("/w.jpg", crew[1].ProfilePath);
        Assert.Equal(2, crew[1].ToPersonItem().Id);
        Assert.Equal(MediaKind.Person, crew[1].ToPersonItem().Kind);
    }

    [Fact]
    public void ImageUris()
    {
        Assert.Equal("https://image.tmdb.org/t/p/w92/abc.jpg", TmdbFormat.ImageUri("/abc.jpg", "w92")?.AbsoluteUri);
        Assert.Null(TmdbFormat.ImageUri(null, "w92"));
        Assert.Equal("https://www.themoviedb.org/tv/1396", TmdbFormat.PageUri(MediaKind.Tv, 1396).AbsoluteUri);
    }
}
