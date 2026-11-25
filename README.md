# AnimeTierlist

A portable Windows desktop app for ranking anime seasons. It fetches a season's shows from [LiveChart](https://www.livechart.me/) in LiveChart's popularity order, downloads their covers, and lets you curate collections, rank shows on tier boards, watch OP and ED videos, and export boards as PNG images.

Projects are stored locally in SQLite. Once a season is fetched, opening, editing, ranking, and exporting all work offline.

## Features

- **Season or year fetch.** Pick a season (winter, spring, summer, fall) or an entire year, and a category: TV, Movies, OVAs, or All. A year combines the four seasons in calendar order, winter first, and keeps each season in LiveChart's popularity order.
- **Exact source order.** Shows keep the order LiveChart lists them in. Ratings, titles, file names, and download completion times never reorder them.
- **Automatic covers.** Covers download in the background at the largest size LiveChart publishes. Missing covers stay visible placeholders that you can retry without affecting show order.
- **Collections.** Each fetch becomes a collection. Include or exclude shows, filter by search, and reorder manually. Excluding a show keeps its cover and leaves existing boards unchanged.
- **Title languages.** Uses LiveChart's displayed title by default. You can switch a collection to English, romaji, or native titles, or set a custom title for an individual show. Titles never change a show's identity or order.
- **Tier boards.** Create multiple boards from one collection. Tiers start as S, A, B, C, and D and can be renamed, recolored, reordered, added, or deleted. Shows start in an Unranked pool and can be ranked by drag-and-drop or keyboard. Duplicate copies on a board show copy numbers.
- **Show details and OP/ED player.** Double-click a show on a board to view its LiveChart details, AniList and MyAnimeList links, and videos with openings and endings first. YouTube videos play inside the app or open in your default browser.
- **LiveChart updates.** **Check LiveChart for updates** previews new shows, shows LiveChart no longer lists, changed titles, and popularity-order changes before applying anything. Your inclusion choices, custom titles, covers, and boards are preserved.
- **Board updates.** **Update from collection...** previews shows to add or remove and lets you refresh titles and covers while keeping existing tier placements.
- **Autosave and undo.** Every change saves to disk automatically. `Ctrl+Z` and `Ctrl+Y` undo and redo edits until you close the project. Failed saves present **Retry save** and **Discard unsaved changes**.
- **Themes.** Supports light, dark, and system high-contrast themes.
- **Exports.**
  - **Export PNG...** renders the board at a fixed width of 1,600 pixels with tier names, covers, and titles. The Unranked section is optional.
  - **Export covers...** copies included covers in collection order into a folder with numbered file names and a JSON manifest.
  - **Export project...** and **Import project...** package a complete project into one `.animerank` archive to move between PCs.

## Download standalone package

Download the latest portable ZIP from [GitHub Releases](https://github.com/Gawron253/AnimeTierlist/releases/latest):

1. Download `AnimeTierlist-<version>-win-x64.zip` and `AnimeTierlist-<version>-win-x64.zip.sha256`.
2. Optional checksum verification in PowerShell:

```powershell
Get-FileHash .\AnimeTierlist-<version>-win-x64.zip -Algorithm SHA256
```

Compare the printed hash with the value in the `.sha256` file.

3. Extract the ZIP to a writable folder, such as `Documents\AnimeTierlist`.
4. Run `AnimeTierlist.exe`.

The standalone package is self-contained. It needs no installer, administrator rights, or separate .NET installation.

## Requirements

- Windows 11 x64 (tested baseline). Windows 10 x64 may work but has not been verified.
- No installation, administrator rights, or separate .NET runtime required for the standalone package.
- In-app YouTube playback uses the Microsoft Edge WebView2 Runtime included with Windows 11. Without it, the player window reports that the runtime is missing and offers **Watch on YouTube** to open the video in your default browser.
- Internet access is needed only to fetch data from LiveChart or play YouTube videos.
- To build from source: the .NET SDK pinned in [global.json](global.json) (10.0.404 or a later 10.0.4xx patch).

## How to use it

1. **Create a project.** Start the app, enter a name under **Create a project**, and select **Create**. Use **Open a project** to return to an existing one.
2. **Fetch a season.** Select **New collection...**, choose a season (or a whole year) and a category, then select **Fetch from LiveChart**. Review the list and select **Create collection**. Covers download in the background; **Stop covers** pauses and **Fetch covers** resumes.
3. **Curate shows.** Every show starts included. Untick shows to exclude them, or use **Include all**, **Exclude all**, or search with **Include shown**. Search filters the view without modifying the collection. Use **Move up**, **Move down**, **To top**, and **To bottom** to adjust order, and **Titles** to switch title languages.
4. **Create a board.** Enter a board name and select **Create board from included shows**. Included shows start in Unranked in collection order.
5. **Rank shows.** Drag shows into tiers, double-click a card for details and OP/ED videos, or use the keyboard with a card selected:

   | Key | Action |
   | --- | --- |
   | `1` to `9` | Move to that tier |
   | `0` | Return to Unranked |
   | `Ctrl+Left` / `Ctrl+Right` | Reorder within the tier |
   | `Alt+Up` / `Alt+Down` | Move to the row above or below (Unranked is the bottom row) |
   | `Ctrl+Z` / `Ctrl+Y` | Undo or redo |

6. **Export.** Use **Export PNG...** on a board, **Export covers...** on a collection, or **Export project...** in the top bar for an `.animerank` file.

## Where data is stored

Projects are stored in `data\projects` next to `AnimeTierlist.exe`. The in-app video player keeps its isolated WebView2 browser profile in `data\webview2`, separate from projects and exports, and you can delete it at any time. To move the app, move the whole folder. If the app folder is read-only, AnimeTierlist asks for another data folder for the current session.

Each project folder contains `project.sqlite`, original covers under `assets\` (named by SHA-256 hash), and regenerable thumbnails under `cache\`. Only one running copy of the app can open a given project at a time.

## Network and privacy

- AnimeTierlist contacts only `www.livechart.me` and `u.livechart.me` over HTTPS, sending `AnimeTierlist/<version>` as the User-Agent, with no cookies and no account.
- Season pages are fetched one at a time, at least five seconds apart, following LiveChart's crawl delay. At most two cover images download concurrently. If LiveChart asks the client to wait, it waits or pauses instead of retrying early.
- Anime details and video links are fetched on demand when you open a show's details window, using the same pacing.
- LiveChart requests connect directly and ignore proxy settings. Networks that allow web access only through a proxy cannot fetch from LiveChart; the app reports this when it happens.
- The in-app video player connects to YouTube only while a player window is open. It uses Windows network and proxy settings, and YouTube may show its own ads or consent prompts.
- AnimeTierlist includes no telemetry, auto-updater, cloud sync, or background scheduler.

## Limitations

- Windows x64 only.
- Boards are ranked and exported locally; there is no TierMaker integration.
- Covers come from LiveChart automatically and cannot be replaced with custom images.
- PNG exports have a fixed width of 1,600 pixels and a maximum size of 40 million pixels. For very large boards, export without Unranked or split the board.
- Undo history lasts until the project is closed.

## Building from source

Clone the repository and build with the pinned .NET SDK:

```powershell
git clone https://github.com/Gawron253/AnimeTierlist.git
cd AnimeTierlist
dotnet restore AnimeTierlist.slnx --locked-mode
dotnet build AnimeTierlist.slnx --no-restore -p:RestoreLockedMode=true
```

Run the desktop app directly from source:

```powershell
dotnet run --project src\AnimeTierlist.Desktop
```

Build the standalone portable ZIP package and its SHA-256 checksum:

```powershell
.\tools\release\Build-Release.ps1
```

This writes `out\AnimeTierlist-<version>-win-x64.zip` and `out\AnimeTierlist-<version>-win-x64.zip.sha256`. See [docs/BUILDING.md](docs/BUILDING.md) for package pinning, vulnerability checks, and CI release details.

## Project layout

| Path | Contents |
| --- | --- |
| `src/AnimeTierlist.Core` | Domain model and rules for items, collections, boards, tiers, and ordering. No UI, SQL, network, or file access. |
| `src/AnimeTierlist.Application` | Application use cases, undo/redo, save coordination, and storage/source/export interfaces. |
| `src/AnimeTierlist.Infrastructure` | LiveChart HTTP client and HTML parsers, SQLite persistence, cover assets, `.animerank` archives, and PNG rendering. |
| `src/AnimeTierlist.Desktop` | WPF views, controls, themes, and view models. Builds `AnimeTierlist.exe`. |
| `tools/release/` | Portable Windows release packaging script. |
| `docs/` | Build guide and the user README packaged inside the standalone release ZIP. |

## Built with

- C#, WPF, and .NET 10
- [AngleSharp](https://anglesharp.github.io/) (MIT)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT)
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) (MIT)
- [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT)
- [Microsoft.Web.WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)
- Seasonal anime metadata and posters from [LiveChart](https://www.livechart.me/)

## License

Licensed under the [MIT License](LICENSE). Third-party component licenses and notices are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
