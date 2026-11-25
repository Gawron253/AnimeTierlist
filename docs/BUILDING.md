# Building AnimeTierlist

Windows x64 only. The .NET SDK version is pinned in [global.json](../global.json) (10.0.404 with latest patch roll-forward). NuGet package versions are pinned in [Directory.Packages.props](../Directory.Packages.props), and each project commits its `packages.lock.json`.

## Restore and build

```powershell
dotnet restore AnimeTierlist.slnx --locked-mode
dotnet build AnimeTierlist.slnx --no-restore -p:RestoreLockedMode=true
```

Locked mode fails restore if a package pin changes without an updated lock file. To update a pin intentionally, run `dotnet restore AnimeTierlist.slnx --force-evaluate`, review the `packages.lock.json` diffs, and commit them with the version change.

Warnings are treated as errors in `Directory.Build.props`.

To check for known vulnerabilities across direct and transitive NuGet dependencies:

```powershell
dotnet list AnimeTierlist.slnx package --vulnerable --include-transitive
```

If Windows Firewall prompts about `dotnet.exe` the first time it contacts `nuget.org`, allow it so restore can finish.

## Run the tests

```powershell
dotnet test --solution AnimeTierlist.slnx
```

The suite covers the core model, storage, LiveChart parsing, HTTP policy, exports, archives, and desktop workflows. It makes no network requests: LiveChart pages come from synthetic fixtures in [tests/fixtures/livechart](../tests/fixtures/livechart/README.md) and covers are generated in memory. Desktop tests render WPF views off-screen, so they need a Windows session.

Opt-in checks are skipped unless their environment variable is set:

| Variable | Check |
| --- | --- |
| `ANIMETIERLIST_LIVE_CHECK=1` | Fetches a small season and its covers from the real LiveChart site, following the app's request pacing. Use it to detect site layout changes. |
| `ANIMETIERLIST_LIVE_PLAYER_CHECK=<links>` | Opens the given YouTube links in the in-app player. |

## Run from source

```powershell
dotnet run --project src/AnimeTierlist.Desktop
```

## Build the standalone portable package

The Desktop project targets `win-x64` as a self-contained publish. Its publish folder includes `AnimeTierlist.exe`, the .NET and WPF runtimes, and the native SQLite and SkiaSharp libraries. It requires no installed .NET runtime or administrator rights.

Run the release script to build the portable ZIP and SHA-256 checksum:

```powershell
.\tools\release\Build-Release.ps1
```

The script writes `out\AnimeTierlist-<version>-win-x64.zip` and `out\AnimeTierlist-<version>-win-x64.zip.sha256`, packaging `README.md`, `LICENSE`, `THIRD_PARTY_NOTICES.md`, and upstream third-party notices under `licenses/`. It does not upload or publish anything.

Running `AnimeTierlist.exe` creates `data\` next to the executable. If that directory is not writable, the app asks for another data directory for the current session.

## Continuous integration and releases

- [.github/workflows/ci.yml](../.github/workflows/ci.yml) runs on every pull request to `main` and every push to `main` on `windows-latest`. It restores with `--locked-mode`, builds `AnimeTierlist.slnx`, runs the test suite, and runs `tools/release/Build-Release.ps1` to verify the standalone package builds cleanly.
- [.github/workflows/release.yml](../.github/workflows/release.yml) runs on `v*` tag pushes and manual workflow dispatches. On a tag push (such as `v1.0.0`, or `v1.0.0-beta.1` for a prerelease), it checks that the tag matches `<Version>` in `src/AnimeTierlist.Desktop/AnimeTierlist.Desktop.csproj`, runs the test suite, builds the standalone ZIP and `.sha256` file, and creates a GitHub Release with both files attached. Versions with a prerelease suffix (`-beta.1`, `-rc.1`) are published as GitHub prereleases. Manual runs build the ZIP and `.sha256` file and upload them as workflow artifacts.
