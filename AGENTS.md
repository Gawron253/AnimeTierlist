# Agent and contributor guide

AnimeTierlist is a portable Windows desktop app (C#/WPF, .NET 10, SQLite) for ranking anime seasons fetched from LiveChart. Read [README.md](README.md) for features and behavior and [docs/BUILDING.md](docs/BUILDING.md) for build, test, and release commands. The code and its tests are the authoritative description of behavior.

## Product rules

- LiveChart is the catalogue authority. Capture its popularity order exactly as served for the requested season and category. Never reorder by rating, title, ID, download completion, click time, or file timestamps.
- A whole-year collection combines seasons in calendar order, winter first, keeps each season's order, and keeps a repeated show at its first occurrence.
- Default titles are LiveChart's displayed titles. Alternate languages and custom titles never change identity or order.
- Collections (selection and order) and each board's tier placements are separate. Refreshing source data never silently changes curation or existing boards; updates are previewed first.
- Projects are local. No cloud service, telemetry, accounts, auto-updater, background scheduling, browser-profile or cookie reuse, or scraping workarounds.
- Respect LiveChart's host policy: catalogue requests one at a time at least five seconds apart, at most two concurrent image requests, honor `Retry-After`, and contact only the allowlisted hosts.

## Architecture

- `src/AnimeTierlist.Core`: domain model and rules. No WPF, SQL, HTTP, or file system access.
- `src/AnimeTierlist.Application`: use cases and narrow interfaces for I/O.
- `src/AnimeTierlist.Infrastructure`: SQLite storage, LiveChart HTTP and parsing, assets, exports, and archives.
- `src/AnimeTierlist.Desktop`: WPF views, view models, and the composition root.

Keep I/O behind Application interfaces and wire implementations at the composition root. Avoid speculative abstractions and generic frameworks.

## Code conventions

- Nullable reference types, immutable IDs, explicit domain and result types.
- Cancellation tokens on background I/O, `Async` suffix on asynchronous methods. Never block the dispatcher or leave fire-and-forget tasks unobserved.
- PascalCase for types and public members, camelCase for parameters and locals, `_camelCase` for private fields.
- WPF code-behind only for view-specific mechanics. Behavior belongs in view models and application operations.
- Warnings are errors (`Directory.Build.props`).

## Persistence and safety

- Serialize project writes. Use parameterized SQL, foreign keys, transactions, and database constraints for invariants. Never key by title or merge distinct source IDs.
- Use explicit integer order fields with deterministic tie rules. Persist source order before parallel cover downloads can affect it.
- An edit counts as saved only after a durable commit. Undo and redo use the same rules as forward edits. A failed save blocks dependent edits until retry or explicit discard.
- Exports use a consistent saved revision and validate temporary output before atomically installing it.
- Project paths are relative and stay inside the project root, including paths read from imported archives. Imports validate schema, limits, paths, images, and domain invariants independently.
- Never silently clear a broken cover reference. Repair it after review or mark it missing.

## Dependencies

Package versions are pinned centrally in `Directory.Packages.props` and every project commits `packages.lock.json`. Add a dependency only for a concrete need, and review licensing and native-code redistribution (update `THIRD_PARTY_NOTICES.md` and `tools/release/Build-Release.ps1` when the shipped files change).

## Tests

- Run `dotnet test --solution AnimeTierlist.slnx` after changes and report the exact command and result. Never claim results you did not observe.
- Add meaningful tests for domain rules, order fidelity, persistence, recovery, hostile inputs, and desktop workflows. Use real temporary SQLite storage for durability tests.
- The default suite makes no network requests. LiveChart pages are synthetic fixtures from `tests/fixtures/livechart/New-SyntheticFixtures.ps1`. Never commit real LiveChart captures, covers, user projects, credentials, or personal paths.
- Live checks against LiveChart or YouTube stay opt-in (see `docs/BUILDING.md`).

## Repository workflow

- Work on a branch and open a pull request to `main`; CI must pass before merging.
- Commit with the GitHub noreply address, never a personal email.
- Releases are tag-driven: set `<Version>` in `src/AnimeTierlist.Desktop/AnimeTierlist.Desktop.csproj` (and the `app.manifest` version), then push tag `v<Version>`. Versions with a suffix such as `-beta.1` publish as prereleases.
- Do not push, tag, publish releases, or rewrite history unless the user asks.
