using Microsoft.Windows.ApplicationModel.Resources;

namespace TrayMDB.Services;

/// <summary>
/// The app's own API keys, compiled into its resources from the gitignored OAuth.resw (copy
/// OAuth.resw.sample to create it). They ship inside the package, so only use this for keys that
/// are meant to live on the client (restricted maps keys, free-tier API keys). A user's own
/// tokens go in <see cref="CredentialStore"/>.
/// </summary>
internal static class Secrets
{
    private static readonly Lazy<string?> s_tmdbApiKey = new(() => GetOAuthString("TmdbApiKey"));

    /// <summary>
    /// The built-in TMDB credential (a v4 read access token or a v3 API key), or null when OAuth.resw
    /// doesn't provide one.
    /// </summary>
    public static string? TmdbApiKey => s_tmdbApiKey.Value;

    private static string? GetOAuthString(string name)
    {
        try
        {
            ResourceLoader loader = new(ResourceLoader.GetDefaultResourceFilePath(), "OAuth");
            string value = loader.GetString(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            // OAuth.resw is missing from this build.
            return null;
        }
    }
}
