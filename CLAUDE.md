@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app built from the `winui-tray-app` skill scaffold on top of `winapp new`. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or secrets.

- Launch with `winapp run` (or `dotnet run`); never register the package by hand. Use `winapp run --clean` to test first-run behavior.
- `App.xaml.cs` owns the tray icon, single instance, and the only exit path (the Exit menu). There is no main window.
- The flyout (`Views/TrayFlyoutWindow`) is hidden on dismiss and closed after a minute hidden. Anything it starts, it must stop in `FlyoutPage.OnHidden` / `Dispose`.
- Network work runs only while visible, through `Services/ForegroundPoller`. The one exception is background work the tray icon itself displays, and that needs a documented reason.
- App API keys go in `OAuth.resw` (gitignored) and are read through `Services/Secrets`. User tokens go in `Services/CredentialStore`. Nothing secret goes in `SettingsService`.
- Put pure decision logic (parsing, policies, formatting) in classes with no WinRT dependency, so a plain `net10.0` test project can cover it.

## Tray MDB

- `Tmdb/` is the TMDB v3 client, models (source-generated JSON), and formatting. It has no WinRT dependency; `tests/TrayMDB.Tests` compiles those files in directly. Run `dotnet test tests/TrayMDB.Tests`.
- The credential is `TmdbApiKey` in `OAuth.resw` (there is no key entry in Settings). Either a v4 read access token (bearer) or a v3 API key (query string) works.
- Seen / want-to-watch flags ("My stuff") are `Tmdb/Library.cs` (pure, tested) saved by `Services/LibraryService` to `library.json` in LocalFolder, not LocalSettings (8 KB cap).
- Settings is a page inside the flyout (`Views/SettingsPage`), not a window.
- TMDB terms require the attribution text on the settings page; keep it.
