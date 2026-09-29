using System.Globalization;
using System.Net.Http;
using TrayMDB.Tmdb;

namespace TrayMDB.Services;

/// <summary>
/// The app's TMDB connection: which key to use, the shared <see cref="HttpClient"/>, and the
/// language and region that results are localized to.
/// </summary>
internal static class TmdbService
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The key built into this app from OAuth.resw, or null when the build has none.</summary>
    public static string? ActiveKey => Secrets.TmdbApiKey;

    /// <summary>A client for the active key, or null when there's no key at all.</summary>
    public static TmdbClient? CreateClient() => ActiveKey is { } key ? new TmdbClient(s_http, key) : null;

    /// <summary>The Windows display language, e.g. "en-US", so titles and overviews come back localized.</summary>
    public static string Language
    {
        get
        {
            string name = CultureInfo.CurrentUICulture.Name;
            return string.IsNullOrEmpty(name) ? "en-US" : name;
        }
    }

    /// <summary>The Windows region, e.g. "US", for age ratings and where-to-watch.</summary>
    public static string Region
    {
        get
        {
            try
            {
                return RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant();
            }
            catch (ArgumentException)
            {
                return "US";
            }
        }
    }
}
