using System.Net;
using System.Text;
using TrayMDB.Tmdb;

namespace TrayMDB.Tests;

public class TmdbClientTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ4In0.sig";

    [Fact]
    public async Task SearchSendsBearerTokenAndKeepsPeople()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"page":1,"total_results":4,"results":[
              {"id":27205,"media_type":"movie","title":"Inception","release_date":"2010-07-15","poster_path":"/p.jpg","vote_average":8.4,"vote_count":37000},
              {"id":525,"media_type":"person","name":"Christopher Nolan","known_for_department":"Directing","profile_path":"/n.jpg",
               "known_for":[{"id":27205,"media_type":"movie","title":"Inception"},{"id":155,"media_type":"movie","title":"The Dark Knight"},{"id":157336,"media_type":"movie","title":"Interstellar"}]},
              {"id":1396,"media_type":"tv","name":"Breaking Bad","first_air_date":"2008-01-20","vote_average":8.9,"vote_count":15000},
              {"id":9,"media_type":"collection","name":"Some Collection"}
            ]}
            """);
        TmdbClient client = new(new HttpClient(handler), Jwt);

        List<TmdbSearchItem> results = await client.SearchAsync("in ception", "en-US", CancellationToken.None);

        Assert.Equal(["Inception", "Christopher Nolan", "Breaking Bad"], results.Select(r => r.DisplayTitle));
        Assert.Equal(MediaKind.Movie, results[0].Kind);
        Assert.Equal("Movie · 2010", results[0].Subtitle);
        Assert.Equal("Director · Inception, The Dark Knight", results[1].Subtitle);
        Assert.Equal("https://image.tmdb.org/t/p/w92/n.jpg", results[1].PosterUri?.AbsoluteUri);
        Assert.Equal(string.Empty, results[1].RatingText);
        Assert.Equal("TV · 2008", results[2].Subtitle);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization?.Scheme);
        Assert.Equal(Jwt, handler.Request.Headers.Authorization?.Parameter);
        Assert.Contains("search/multi?query=in%20ception", handler.Request.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("api_key", handler.Request.RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task TrendingLeavesPeopleOut()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"results":[{"id":1,"media_type":"person","name":"Someone"},{"id":2,"media_type":"movie","title":"A Film"}]}
            """);
        TmdbClient client = new(new HttpClient(handler), Jwt);

        List<TmdbSearchItem> results = await client.TrendingAsync("en-US", CancellationToken.None);

        Assert.Equal(["A Film"], results.Select(r => r.DisplayTitle));
    }

    [Fact]
    public async Task PersonReadsCombinedCreditsAndImdbId()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"id":6193,"name":"Leonardo DiCaprio","birthday":"1974-11-11","place_of_birth":"Los Angeles, California, USA",
             "known_for_department":"Acting","biography":"An actor.","profile_path":"/l.jpg",
             "combined_credits":{"cast":[{"id":27205,"media_type":"movie","title":"Inception","character":"Cobb","vote_count":37000}],"crew":[]},
             "external_ids":{"imdb_id":"nm0000138"}}
            """);
        TmdbClient client = new(new HttpClient(handler), Jwt);

        TmdbPerson person = await client.GetPersonAsync(6193, "en-US", CancellationToken.None);

        Assert.Contains("person/6193?append_to_response=combined_credits,external_ids", handler.Request!.RequestUri!.OriginalString);
        Assert.Equal("nm0000138", person.ImdbId);
        Assert.Equal("https://www.imdb.com/name/nm0000138/", TmdbFormat.ImdbUri(person.ImdbId)?.AbsoluteUri);
        Assert.Equal("Cobb", TmdbFormat.KnownFor(person, 10).Single().RoleText);
    }

    [Fact]
    public async Task V3KeyGoesInTheQueryString()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """{"results":[]}""");
        TmdbClient client = new(new HttpClient(handler), "0123456789abcdef0123456789abcdef");

        await client.TrendingAsync("en-US", CancellationToken.None);

        Assert.Null(handler.Request!.Headers.Authorization);
        Assert.Contains("api_key=0123456789abcdef0123456789abcdef", handler.Request.RequestUri!.Query);
    }

    [Fact]
    public async Task UnauthorizedThrowsAuthException()
    {
        FakeHandler handler = new(HttpStatusCode.Unauthorized, """{"status_code":7,"status_message":"Invalid API key: You must be granted a valid key."}""");
        TmdbClient client = new(new HttpClient(handler), "bad");

        TmdbAuthException ex = await Assert.ThrowsAsync<TmdbAuthException>(() => client.TrendingAsync("en-US", CancellationToken.None));
        Assert.StartsWith("Invalid API key", ex.Message);
    }

    [Fact]
    public async Task TvDetailsReadAppendedResponses()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20","last_air_date":"2013-09-29","in_production":false,
             "number_of_seasons":5,"genres":[{"id":18,"name":"Drama"}],"created_by":[{"id":66633,"name":"Vince Gilligan"}],
             "credits":{"cast":[{"name":"Bryan Cranston","character":"Walter White","profile_path":"/b.jpg"}],"crew":[]},
             "content_ratings":{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]},
             "external_ids":{"imdb_id":"tt0903747"},
             "watch/providers":{"results":{"US":{"link":"https://www.themoviedb.org/tv/1396/watch?locale=US","flatrate":[{"provider_name":"Netflix","display_priority":0}]}}}}
            """);
        TmdbClient client = new(new HttpClient(handler), Jwt);

        TmdbDetails details = await client.GetDetailsAsync(MediaKind.Tv, 1396, "en-US", CancellationToken.None);

        Assert.Contains("tv/1396?append_to_response=credits,videos,content_ratings,external_ids,watch/providers", handler.Request!.RequestUri!.OriginalString);
        Assert.Equal(MediaKind.Tv, details.Kind);
        Assert.Equal("tt0903747", details.ImdbId);
        Assert.Equal("TV · 2008–2013 · TV-MA · 5 seasons", TmdbFormat.MetaLine(details, "US"));
        Assert.Equal("Created by Vince Gilligan", TmdbFormat.Credit(details));
        Assert.Equal("Stream on Netflix", TmdbFormat.WatchSummary(details, "US"));
        Assert.Equal("Walter White", details.Credits!.Cast[0].Character);
    }

    [Theory]
    [InlineData(Jwt, true)]
    [InlineData("0123456789abcdef0123456789abcdef", false)]
    public void RecognizesCredentialKinds(string credential, bool bearer) => Assert.Equal(bearer, TmdbClient.IsBearerToken(credential));

    private sealed class FakeHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
