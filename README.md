# AnimeTierlist

A portable Windows desktop app for ranking anime seasons. It fetches a season's shows from [LiveChart](https://www.livechart.me/) in LiveChart's popularity order, downloads their covers, and lets you curate collections, rank shows on tier boards, watch OP and ED videos, and export boards as PNG images.

Projects are stored locally in SQLite. Once a season is fetched, opening, editing, ranking, and exporting work offline. Source code and updates are at https://github.com/Gawron253/AnimeTierlist.

## Features

- **Season and year fetches.** Pick a season (winter, spring, summer, fall) or a full year, and filter by TV, Movies, OVAs, or All. Shows keep LiveChart's popularity order.
- **Cover downloads.** Covers download in the background at the largest size LiveChart publishes. Missing covers stay as placeholders you can retry without changing show order.
- **Collections and title languages.** Include or exclude shows, filter by search, reorder manually, and switch between LiveChart's default title, English, romaji, native, or custom per-show titles.
- **Tier boards.** Create multiple boards from one collection. Rename, recolor, reorder, add, or delete tiers, rank by drag-and-drop or keyboard, and place duplicate copies on a board when needed.
- **Show details and OP/ED player.** Double-click a show on a board to view its LiveChart details, AniList and MyAnimeList links, and videos with openings and endings first. YouTube videos play inside the app or open in your default browser.
- **LiveChart and board updates.** **Check LiveChart for updates** previews added, removed, renamed, or reordered shows before applying changes. **Update from collection...** syncs a board while keeping existing tier placements.
- **Autosave, undo, and themes.** Every change saves automatically, `Ctrl+Z` and `Ctrl+Y` undo and redo edits until you close the project, and the UI supports light, dark, and system high-contrast themes.
- **Export formats.** Save a board as a 1,600-pixel-wide PNG (with or without Unranked), copy included covers into a numbered folder with a JSON manifest, or package and import full projects as `.animerank` archives.

## Download

Download the portable ZIP from [GitHub Releases](https://github.com/Gawron253/AnimeTierlist/releases):

1. Download `AnimeTierlist-<version>-win-x64.zip`.
2. Extract the ZIP to a writable folder, such as `Documents\AnimeTierlist`.
3. Run `AnimeTierlist.exe`.

An optional `.sha256` checksum file is included with each release if you want to verify the download.

## Requirements

- Windows 11 x64 (tested baseline). Windows 10 x64 may work but has not been verified.
- No installer, administrator rights, or separate .NET runtime is required. Everything the app needs is in the extracted folder.
- In-app YouTube playback uses the Microsoft Edge WebView2 Runtime included with Windows 11. Without it, the player window reports that the runtime is missing and offers **Watch on YouTube** to open the video in your default browser.
- Internet access is needed only to fetch data from LiveChart or play YouTube videos.

## License notice

AnimeTierlist is licensed under the MIT License ([LICENSE](LICENSE)). By using or redistributing the standalone package, you agree to the bundled Microsoft .NET Library License (`licenses\microsoft\MICROSOFT_DOTNET_LIBRARY_LICENSE.txt`) and Microsoft Windows SDK License (`licenses\microsoft\WINDOWS_SDK_LICENSE.txt`) for the six Microsoft runtime DLLs listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and to the remaining third-party terms in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `licenses\`.

## How to use it

1. **Create a project.** Start `AnimeTierlist.exe`, enter a name under **Create a project**, and select **Create**. Use **Open a project** to return to an existing one.
2. **Fetch a season.** Select **New collection...**, choose a season (or a whole year) and a category, then select **Fetch from LiveChart**. Review the list and select **Create collection**. Covers download in the background; **Stop covers** pauses and **Fetch covers** resumes.
3. **Curate shows.** Every show starts included. Untick shows to exclude them, or use **Include all**, **Exclude all**, or search with **Include shown**. Search filters the view without modifying the collection. Use **Move up**, **Move down**, **To top**, and **To bottom** to adjust order, and **Titles** to switch title languages.
4. **Create a board.** Enter a board name and select **Create board from included shows**. Included shows start in Unranked in collection order.
5. **Rank shows.** Drag shows into tiers, double-click a card for details and OP/ED videos, or use the keyboard with a card selected:

   | Key | Action |
   | --- | --- |
   | `1` to `9` | Move to that tier |
   | `0` | Return to Unranked |
   | `Ctrl+Left` and `Ctrl+Right` | Reorder within the tier |
   | `Alt+Up` and `Alt+Down` | Move to the row above or below (Unranked is the bottom row) |
   | `Ctrl+Z` and `Ctrl+Y` | Undo or redo |

   If an uploader blocks embedded playback on YouTube, the player says so and offers **Watch on YouTube** to open the video in your browser. Every change saves automatically; if a save fails, the toolbar offers **Retry save** and **Discard unsaved changes**.
6. **Export.** Use **Export PNG...** on a board, **Export covers...** on a collection, or **Export project...** in the top bar for an `.animerank` file.

## Where data is stored

Projects are stored in `data\projects` next to `AnimeTierlist.exe`. The in-app video player keeps its isolated WebView2 browser profile (YouTube cookies and cache) in `data\webview2`, separate from projects and exports, and you can delete it at any time. To move the app, move the whole folder. If the app folder is read-only, AnimeTierlist asks for another data folder for the current session without saving that choice.

Each project folder contains `project.sqlite`, original covers under `assets\` (named by SHA-256 hash), and regenerable thumbnails under `cache\`. Only one running copy of the app can open a given project at a time.

## Network and privacy

- For season, cover, and show data, AnimeTierlist contacts only `www.livechart.me` and `u.livechart.me` over HTTPS and sends `AnimeTierlist/<version>` as the User-Agent, with no cookies and no account.
- Season pages are fetched one at a time, at least five seconds apart, to respect LiveChart's crawl delay. At most two cover images download concurrently. If LiveChart asks the client to wait, it waits or pauses instead of retrying early.
- Show details and video links are fetched on demand when you open a show's details window, with the same pacing.
- LiveChart requests connect directly and ignore proxy settings. Networks that allow web access only through a proxy cannot fetch from LiveChart; the app reports this when it happens.
- The in-app video player connects to YouTube only while a player window is open. It uses Windows network and proxy settings, and YouTube may show its own ads or consent prompts.
- AnimeTierlist includes no telemetry, auto-updater, cloud sync, or background scheduler. The WebView2 Runtime sends its own diagnostic data to Microsoft according to your Windows privacy settings.

## Limitations

- Windows x64 only.
- Boards are ranked and exported locally; there is no TierMaker integration.
- Covers come from LiveChart automatically and cannot be replaced with custom images.
- PNG exports have a fixed width of 1,600 pixels and a maximum size of 40 million pixels. For very large boards, export without Unranked or split the board.
- Undo history lasts until the project is closed.

## Building from source

See [docs/BUILDING.md](https://github.com/Gawron253/AnimeTierlist/blob/main/docs/BUILDING.md) for SDK requirements, build commands, and standalone release packaging.
