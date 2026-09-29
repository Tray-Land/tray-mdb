using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TrayMDB.Tmdb;

/// <summary>TMDB rejected the key (HTTP 401): it's wrong, revoked, or missing.</summary>
public sealed class TmdbAuthException(string? message) : Exception(message ?? "TMDB rejected the API key.");

/// <summary>
/// A thin client for the few TMDB v3 endpoints the app uses. Accepts either kind of TMDB credential:
/// a v4 "API Read Access Token" (a JWT, sent as a bearer token) or a v3 "API Key" (sent as a query
/// parameter). No WinRT, so tests drive it through a fake <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class TmdbClient(HttpClient http, string credential)
{
    public static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");

    /// <summary>Movies, shows, and people matching <paramref name="query"/>, in TMDB's relevance order.</summary>
    public async Task<List<TmdbSearchItem>> SearchAsync(string query, string language, CancellationToken cancellationToken)
    {
        string path = $"search/multi?query={Uri.EscapeDataString(query)}&include_adult=false&page=1&language={Uri.EscapeDataString(language)}";
        TmdbPage<TmdbSearchItem> page = await GetAsync(path, TmdbJsonContext.Default.TmdbPageTmdbSearchItem, cancellationToken);
        return Displayable(page.Results, includePeople: true);
    }

    /// <summary>Today's trending movies and shows (people are left out).</summary>
    public async Task<List<TmdbSearchItem>> TrendingAsync(string language, CancellationToken cancellationToken)
    {
        string path = $"trending/all/day?language={Uri.EscapeDataString(language)}";
        TmdbPage<TmdbSearchItem> page = await GetAsync(path, TmdbJsonContext.Default.TmdbPageTmdbSearchItem, cancellationToken);
        return Displayable(page.Results, includePeople: false);
    }

    /// <summary>A person's bio, combined movie and TV credits, and IMDb ID, in one request.</summary>
    public async Task<TmdbPerson> GetPersonAsync(int id, string language, CancellationToken cancellationToken)
    {
        string path = $"person/{id}?append_to_response=combined_credits,external_ids&language={Uri.EscapeDataString(language)}";
        TmdbPerson person = await GetAsync(path, TmdbJsonContext.Default.TmdbPerson, cancellationToken);
        person.ImdbId ??= person.ExternalIds?.ImdbId;
        return person;
    }

    /// <summary>Everything the detail view shows, in one request via <c>append_to_response</c>.</summary>
    public async Task<TmdbDetails> GetDetailsAsync(MediaKind kind, int id, string language, CancellationToken cancellationToken)
    {
        string append = kind == MediaKind.Tv
            ? "credits,videos,content_ratings,external_ids,watch/providers"
            : "credits,videos,release_dates,watch/providers";
        string path = $"{TmdbFormat.PathSegment(kind)}/{id}?append_to_response={append}&language={Uri.EscapeDataString(language)}";
        TmdbDetails details = await GetAsync(path, TmdbJsonContext.Default.TmdbDetails, cancellationToken);
        details.Kind = kind;
        details.ImdbId ??= details.ExternalIds?.ImdbId;
        return details;
    }

    /// <summary>A v4 read access token is a JWT; a v3 API key is 32 hex characters.</summary>
    public static bool IsBearerToken(string credential) => credential.StartsWith("eyJ", StringComparison.Ordinal) && credential.Contains('.');

    private static List<TmdbSearchItem> Displayable(List<TmdbSearchItem> results, bool includePeople) =>
        results
            .Where(r => r.Kind is MediaKind.Movie or MediaKind.Tv || (includePeople && r.Kind == MediaKind.Person))
            .Where(r => !string.IsNullOrWhiteSpace(r.DisplayTitle))
            .ToList();

    private async Task<T> GetAsync<T>(string relative, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        string trimmed = credential.Trim();
        bool bearer = IsBearerToken(trimmed);
        if (!bearer)
        {
            relative += $"&api_key={Uri.EscapeDataString(trimmed)}";
        }

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(BaseAddress, relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmed);
        }

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new TmdbAuthException(await ReadErrorAsync(body, cancellationToken));
        }

        if (!response.IsSuccessStatusCode)
        {
            string? message = await ReadErrorAsync(body, cancellationToken);
            throw new HttpRequestException(message ?? $"TMDB returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return await JsonSerializer.DeserializeAsync(body, typeInfo, cancellationToken)
            ?? throw new HttpRequestException("TMDB returned an empty response.");
    }

    private static async Task<string?> ReadErrorAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            TmdbError? error = await JsonSerializer.DeserializeAsync(body, TmdbJsonContext.Default.TmdbError, cancellationToken);
            return error?.StatusMessage;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
