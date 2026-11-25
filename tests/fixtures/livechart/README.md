# LiveChart test fixtures

Synthetic season pages for the parser, HTTP adapter, and desktop workflow tests. Each `<name>.html` has a `<name>.expected.json` oracle.

The pages reproduce only the markup structure the parser reads: canonical link, `seasonal-chart` container, `Sort: Popularity` control, `article[data-anime-id]` cards with title attributes, poster `srcset`, rating overlay, and synopsis. Every show, title, ID, rating, date, and image URL is invented. No LiveChart content is included.

## Regenerating

```powershell
pwsh -NoProfile -File tests/fixtures/livechart/New-SyntheticFixtures.ps1
```

The script is deterministic. It writes each oracle from its own show list, never from the parser under test. Edit the script instead of the generated files.

| Fixture | Cards | Covers |
| --- | ---: | --- |
| `summer-2025-tv` | 30 | Order, displayed titles, English and romanized fallback titles, ratings that differ from popularity order, one synopsis |
| `summer-2025-movies` | 14 | Desktop workflows. Titles with apostrophes, `/`, `?`, `:`, and non-ASCII characters |
| `summer-2025-ovas` | 10 | Category route and order |
| `summer-2025-all` | 54 | TV, Movies, and OVAs interleaved, with each category's order preserved |
| `winter-2025-tv` | 20 | A second season |
| `fall-2027-movies` | 0 | Verified empty season: valid page, chart container and sort control present, no show cards |

Every page ends with a non-show tutorial card that the parser must skip. The tests derive changed-layout, wrong-sort, wrong-route, missing-title, and foreign-image-host cases from these pages. An unknown season (HTTP 404) is tested with an inline document.

The opt-in live check `LiveAcquisitionCheck` (`ANIMETIERLIST_LIVE_CHECK=1`) runs the parser against the real site to detect layout changes. It is never part of the default suite.
