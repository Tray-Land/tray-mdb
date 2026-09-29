using TrayMDB.Tmdb;
using Windows.Storage;

namespace TrayMDB.Services;

/// <summary>
/// The user's <see cref="Library"/>, kept in <c>library.json</c> in LocalFolder rather than
/// LocalSettings, which caps a value at 8 KB. Loaded on first use and saved after every change.
/// </summary>
internal static class LibraryService
{
    private const string FileName = "library.json";

    private static Library? s_library;

    public static Library Library => s_library ??= Load();

    public static void SetSeen(LibraryEntry title, bool seen)
    {
        Library.SetSeen(title, seen, DateTimeOffset.Now);
        Save();
    }

    public static void SetWantToWatch(LibraryEntry title, bool wantToWatch)
    {
        Library.SetWantToWatch(title, wantToWatch, DateTimeOffset.Now);
        Save();
    }

    private static string FilePath => Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);

    private static Library Load()
    {
        try
        {
            return File.Exists(FilePath) ? Library.FromJson(File.ReadAllText(FilePath)) : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    // A few KB, so a synchronous write is fine. Write-then-move so a crash never leaves half a file.
    private static void Save()
    {
        try
        {
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, Library.ToJson());
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the flags stay in memory for this session.
        }
    }
}
