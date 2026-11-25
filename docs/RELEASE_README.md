# AnimeTierlist

A portable Windows app for ranking anime seasons. It reads each season's shows from LiveChart in LiveChart's popularity order, lets you choose and order the shows you care about, rank them on tier boards, watch OP and ED videos, and export the result.

Source code and updates: https://github.com/Gawron253/AnimeTierlist

## Requirements

- Windows 11, 64-bit (x64), the tested baseline. Windows 10 x64 may work but has not been verified.
- No installation, administrator rights, or separate .NET runtime are needed. Everything the app uses is in this folder. Playing videos inside the app uses the Microsoft Edge WebView2 Runtime that comes with Windows 11; without it, the player window reports that the runtime is missing and offers **Watch on YouTube** to open the video in your browser.
- An internet connection only for fetching seasons, details, and covers from LiveChart or playing YouTube videos. Opening, editing, ranking, and exporting work offline.

## License notice

AnimeTierlist is licensed under the MIT License (LICENSE). By using or redistributing this standalone package, you agree to the bundled Microsoft .NET Library License (licenses\microsoft\MICROSOFT_DOTNET_LIBRARY_LICENSE.txt) and Microsoft Windows SDK License (licenses\microsoft\WINDOWS_SDK_LICENSE.txt) for the six Microsoft runtime DLLs listed in THIRD_PARTY_NOTICES.md, and to the remaining third-party terms in THIRD_PARTY_NOTICES.md and licenses\.

## Getting started

1. Extract the ZIP to a folder you can write to, for example `Documents\AnimeTierlist`.
2. Run `AnimeTierlist.exe`.
3. Create a project, choose a season (or a whole year) and a category, select **Fetch from LiveChart**, review the order, and create the collection. Covers download automatically.
4. Every show starts included. Exclude the ones you do not want, adjust the order if you like, then create a board from the included shows.
5. Rank shows by dragging them or with the keyboard. With a show selected: `1` to `9` move it to that tier, `0` returns it to Unranked, `Ctrl+Left` and `Ctrl+Right` reorder it within its tier, and `Alt+Up` and `Alt+Down` move it to the row above or below.
6. Export the board as PNG, export the included covers as a folder, or export the whole project as an `.animerank` file.

Double-click a show on a board to see its LiveChart details, AniList and MyAnimeList links, and its videos, with openings and endings first. Select a YouTube video to play it inside the app, or use the button next to it to open it in your browser. If the uploader does not allow playback outside YouTube, the player says so and offers **Watch on YouTube**.

Every change saves automatically; the toolbar shows **Saving...**, **Saved**, or a failed save with Retry and Discard. `Ctrl+Z` and `Ctrl+Y` undo and redo changes until you close the project.

## Where your data is

Projects are stored in `data\projects` next to `AnimeTierlist.exe`. The in-app video player keeps its own browser data (YouTube's cookies and cache) in `data\webview2`. It is never part of a project or export, and you can delete it at any time. To move the app, move the whole folder. If the folder is read-only, the app asks for another data folder for that session; it does not remember the choice.

## Network behavior

- The app contacts only `www.livechart.me` and `u.livechart.me` over HTTPS, and identifies itself as `AnimeTierlist/<version>`.
- Season pages are requested one at a time, at least five seconds apart; at most two cover images download at once. If LiveChart asks the app to wait, it waits or pauses instead of retrying early.
- It connects directly and does not use proxy servers. Networks that allow web access only through a proxy cannot fetch from LiveChart; the app says so when that happens.
- Details and videos for a show are fetched from LiveChart only when you open that show's details, with the same pacing.
- The in-app video player connects to YouTube only while a player window is open. It uses Windows' normal network and proxy settings, and YouTube may show its own ads and consent screen.
- The app has no telemetry, no updater, and no account, and sends no cookies to LiveChart. The WebView2 Runtime sends its own diagnostic data to Microsoft according to your Windows privacy settings.

## Limitations

- Windows x64 only.
- No TierMaker integration. Boards are ranked and exported locally.
- Titles are LiveChart's displayed titles, with English, romaji, and native titles where LiveChart lists them; custom titles are available per show.
- The PNG export width is fixed at 1,600 pixels, and very large boards are limited to 40 million pixels (export without the unranked shows, or split the board).
- Undo history lasts until the project is closed.

## Verifying the download

The release includes `AnimeTierlist-<version>-win-x64.zip.sha256`. In PowerShell, `Get-FileHash .\AnimeTierlist-<version>-win-x64.zip -Algorithm SHA256` must print the same hash.

## License

AnimeTierlist is licensed under the MIT License (see `LICENSE`). Bundled Microsoft Windows runtime binaries and third-party libraries are governed by their own terms in `THIRD_PARTY_NOTICES.md` and `licenses\`, including the Microsoft .NET Library License and Windows SDK License for six Microsoft runtime DLLs.
