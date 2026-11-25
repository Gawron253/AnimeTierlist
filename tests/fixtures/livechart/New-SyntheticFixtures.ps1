#Requires -Version 7.0
<#
.SYNOPSIS
    Regenerates the synthetic LiveChart-style season pages and their expected-value oracles.

.DESCRIPTION
    The pages copy only the markup structure the parser reads (canonical link, chart container, sort control, card
    attributes, poster srcset, rating overlay, synopsis). Every show, title, ID, rating, date, and image URL is invented,
    so the fixtures contain no LiveChart content. Output is deterministic: running the script twice produces identical
    files. The oracle in each <name>.expected.json comes from the show list below, never from the parser under test.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Words = @(
    'Amber', 'Basalt', 'Cobalt', 'Driftwood', 'Ember', 'Fennel', 'Garnet', 'Harbor', 'Indigo', 'Juniper',
    'Kestrel', 'Lantern', 'Meadow', 'Nimbus', 'Orchid', 'Pebble', 'Quartz', 'Rowan', 'Saffron', 'Thistle',
    'Umber', 'Vellum', 'Willow', 'Xenon', 'Yarrow', 'Zephyr', 'Aurora', 'Bramble', 'Cinder', 'Dewdrop',
    'Estuary', 'Foxglove', 'Glacier', 'Hollow', 'Ironwood', 'Jasper', 'Kelp', 'Lichen', 'Marble', 'Nectar',
    'Obsidian', 'Paprika', 'Quill', 'Russet', 'Sorrel', 'Tundra', 'Upland', 'Verdant', 'Wisteria', 'Yonder',
    'Acorn', 'Beacon', 'Canyon', 'Dune', 'Eddy', 'Fjord', 'Grotto', 'Heron', 'Islet', 'Jetty',
    'Knoll', 'Lagoon', 'Mesa', 'Nook', 'Oasis', 'Prairie', 'Quarry', 'Reef', 'Spire', 'Thicket',
    'Uplift', 'Vale', 'Wharf', 'Yucca', 'Zinnia', 'Alcove', 'Bluff', 'Cove', 'Delta', 'Ember Isle'
)
$Suffixes = @('Chronicle', 'Academy', 'Patrol', 'Garden', 'Express', 'Requiem', 'Saga', 'Club', 'Station', 'Voyage')
$Kana = @('あ', 'か', 'さ', 'た', 'な', 'は', 'ま', 'や', 'ら', 'わ')

# Titles exercising HTML entities, typographic punctuation, and characters that are invalid in Windows file names.
$SpecialTitles = @{
    1 = "Pebble's Long Summer: The Movie"
    3 = 'Quartz/Night – Encore?'
    5 = 'Rowan ☆ Starlight Revue'
}

function New-Show {
    param([int]$Index, [int]$Id, [string]$Format)

    $first = $Words[$Index % $Words.Count]
    $suffix = $Suffixes[($Index * 3) % $Suffixes.Count]
    $romanized = "$first no $suffix $Index"
    $hasEnglish = ($Index % 3) -ne 2
    $english = if ($hasEnglish) { "The $first $suffix $Index" } else { $null }
    if ($Format -eq 'movies' -and $SpecialTitles.ContainsKey($Index % 7)) {
        $english = "$($SpecialTitles[$Index % 7]) $Index"
        $hasEnglish = $true
    }

    $native = ($Kana[$Index % 10] + $Kana[($Index * 7) % 10] + $Kana[($Index * 3) % 10]) + "テスト $Index"
    [pscustomobject]@{
        Id          = $Id
        Format      = $Format
        Romanized   = $romanized
        English     = $english
        Native      = $native
        Displayed   = if ($hasEnglish) { $english } else { $romanized }
        Alternates  = @("$first $suffix (alt) $Index", "[KO] 테스트 $Index")
        Premiere    = 1735689600 + ($Index * 86400) + ($Id % 3600)
        Rating      = $null
        RatingCount = 0
        Synopsis    = $null
        PosterHash  = ([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes("synthetic-poster-$Id")) |
            ForEach-Object { $_.ToString('x2') }) -join '' | ForEach-Object { $_.Substring(0, 32) }
    }
}

function New-ShowList {
    param([int]$Count, [int]$Seed, [int]$IdBase, [string]$Format)

    # IDs are deliberately not in ascending order, so a parser that sorts by ID fails the order oracle.
    0..($Count - 1) | ForEach-Object {
        New-Show -Index ($Seed + $_) -Id ($IdBase + (($_ * 37 + $Seed) % 97)) -Format $Format
    }
}

function Set-Ratings {
    param([object[]]$Shows, [int]$Count)

    for ($i = 0; $i -lt [Math]::Min($Count, $Shows.Count); $i++) {
        # Ratings vary independently of position, so they are never the popularity order.
        $Shows[$i].Rating = (6.5 + ((($i * 53) + 17) % 250) / 100.0).ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture)
        $Shows[$i].RatingCount = 400 + ($i * 911) % 5000
    }
}

function ConvertTo-Attr([string]$Value) { [System.Net.WebUtility]::HtmlEncode($Value) }

function Format-Card {
    param($Show)

    $id = $Show.Id
    $alternates = ConvertTo-Attr (ConvertTo-Json -InputObject @($Show.Alternates) -Compress)
    $english = if ($Show.English) { " data-anime-item-english-title=`"$(ConvertTo-Attr $Show.English)`"" } else { '' }
    $poster = "https://u.livechart.me/anime/$id/poster_image/$($Show.PosterHash).webp"
    $rating = ''
    if ($Show.Rating) {
        $count = $Show.RatingCount.ToString('N0', [System.Globalization.CultureInfo]::InvariantCulture)
        $rating = "<div class=`"lc-anime-poster-overlay-host__overlay lc-anime-poster-overlay-host__rating lcm3-text lcm3-text--emphasized lcm3-text--label-md`" data-action=`"click->anime-item#openListEditor`" title=`"$($Show.Rating) out of 10 based on $count user ratings`">$($Show.Rating)</div>"
    }

    $synopsis = if ($Show.Synopsis) { "<div class=`"lc-anime-details-card__synopsis`"><p>$(ConvertTo-Attr $Show.Synopsis)</p></div>" } else { '' }
    "<article data-anime-id=`"$id`" data-anime-card-grid-target=`"card`" data-mark-filterable-collection-target=`"node`" data-filterable-collection-target=`"node`" class=`"lc-anime-card-abs lc-anime-details-card lcm3-card lcm3-card--elevated`" data-controller=`"anime-item`" data-anime-item-id=`"$id`" data-anime-item-romanized-native-title=`"$(ConvertTo-Attr $Show.Romanized)`"$english data-anime-item-native-title=`"$(ConvertTo-Attr $Show.Native)`" data-anime-item-alternate-titles=`"$alternates`" data-anime-item-premiere-value=`"$($Show.Premiere)`" data-anime-item-premiere-precision=`"4`" data-theme=`"library-mark`">" +
    "<a data-anime-item-target=`"preferredTitle`" class=`"lc-anime-details-card__title lcm3-text line-clamp-2 lcm3-text--title-md lc-link lc-link-hover`" href=`"/anime/$id`">$(ConvertTo-Attr $Show.Displayed)</a>" +
    "<img src=`"$poster/small.jpg`" data-anime-item-target=`"poster`" class=`"lc-anime-details-card__poster lc-aspect-poster lc-image`" srcset=`"$poster/small.jpg 1x, $poster/large.jpg 2x`" loading=`"lazy`" decoding=`"async`" width=`"175`" height=`"250`">" +
    "<div class=`"lc-anime-poster-overlay-host lc-anime-details-card__poster-overlays flex flex-col gap-4dp pb-4dp overflow-hidden`" data-theme=`"media-overlay`"><div class=`"flex-1 pointer-events-none`"></div>$rating</div>$synopsis</article>"
}

function Write-Page {
    param([string]$Name, [string]$Season, [int]$Year, [string]$Category, [object[]]$Shows, [hashtable]$Extra = @{})

    $seasonTitle = [System.Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase($Season)
    $categoryTitle = @{ tv = 'TV'; movies = 'Movies'; ovas = 'OVAs'; all = 'All' }[$Category]
    $lines = @(
        '<!DOCTYPE html>'
        '<!-- Synthetic LiveChart-style page for automated tests, generated by New-SyntheticFixtures.ps1. All shows are invented. -->'
        '<html><head>'
        "<title>$seasonTitle $Year Anime Chart - $categoryTitle | LiveChart.me</title>"
        "<link rel=`"canonical`" href=`"/$Season-$Year/$Category`" />"
        '</head><body><main>'
        '<span class="contents" data-controller="anime-page-search seasonal-chart mark-filterable-collection filterable-collection">'
        '<button popovertarget="sort-by-overlay"><span class="lcm3-button__label shrink text-center truncate">Sort: Popularity</span></button>'
    )
    $lines += @($Shows | ForEach-Object { Format-Card $_ })
    # A non-show tutorial card, as the real chart ends with; the parser must skip it.
    $lines += '<article class="grid lc-tutorial-card py-16dp lcm3-card lcm3-card--outlined"><div class="lc-tutorial-card__title">Explore with more filters</div><a href="/anime?season=' + $Season + '&amp;year=' + $Year + '" class="lc-tutorial-card__action"><span class="lcm3-button__label shrink text-center truncate">Explore Anime</span></a></article>'
    $lines += '</span></main></body></html>'
    [System.IO.File]::WriteAllText((Join-Path $OutputDirectory "$Name.html"), ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

    $expected = [ordered]@{
        route           = "https://www.livechart.me/$Season-$Year/$Category"
        season          = $Season
        year            = $Year
        category        = $Category
        source          = 'synthetic; generated by New-SyntheticFixtures.ps1'
        sortLabel       = 'Sort: Popularity'
        expectedOutcome = if ($Shows.Count -eq 0) { 'verified-empty' } else { 'complete' }
        oracle          = [ordered]@{ orderedIds = @($Shows | ForEach-Object Id) }
    }
    if ($Shows.Count -gt 0) { $expected.oracle.displayedTitles = @($Shows | ForEach-Object Displayed) }
    foreach ($key in $Extra.Keys | Sort-Object) { $expected[$key] = $Extra[$key] }
    $json = ConvertTo-Json -InputObject $expected -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $OutputDirectory "$Name.expected.json"), ($json -replace "`r`n", "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
}

# Summer 2025: three categories with disjoint IDs, merged into All with each category's order preserved.
$tv = @(New-ShowList -Count 30 -Seed 0 -IdBase 910000 -Format 'tv')
$movies = @(New-ShowList -Count 14 -Seed 40 -IdBase 920000 -Format 'movies')
$ovas = @(New-ShowList -Count 10 -Seed 60 -IdBase 930000 -Format 'ovas')
Set-Ratings $tv 12
Set-Ratings $movies 3
$tv[15].Synopsis = 'A lighthouse keeper and a stray cartographer redraw a coastline that changes every night.'

$all = [System.Collections.Generic.List[object]]::new()
$parts = @($tv, $movies, $ovas)
$next = @(0, 0, 0)
$pattern = @(0, 0, 1, 0, 2, 0, 1)
for ($step = 0; $all.Count -lt ($tv.Count + $movies.Count + $ovas.Count); $step++) {
    $part = $pattern[$step % $pattern.Count]
    if ($next[$part] -ge $parts[$part].Count) {
        $part = @(0, 1, 2) | Where-Object { $next[$_] -lt $parts[$_].Count } | Select-Object -First 1
    }

    $all.Add($parts[$part][$next[$part]])
    $next[$part]++
}

$english = $tv | Where-Object English | Select-Object -First 1
$fallback = $tv | Where-Object { -not $_.English } | Select-Object -First 1
Write-Page 'summer-2025-tv' 'summer' 2025 'tv' $tv @{
    languageExample          = [ordered]@{ id = $english.Id; displayed = $english.Displayed; english = $english.English; romanized = $english.Romanized; native = $english.Native }
    romanizedFallbackExample = [ordered]@{ id = $fallback.Id; displayed = $fallback.Displayed }
    ratingIsNotOrder         = [ordered]@{ position0Rating = $tv[0].Rating; position10Id = $tv[10].Id; position10Rating = $tv[10].Rating }
    detailsExample           = [ordered]@{ position0Rating = $tv[0].Rating; synopsisId = $tv[15].Id; synopsis = $tv[15].Synopsis }
}
Write-Page 'summer-2025-movies' 'summer' 2025 'movies' $movies
Write-Page 'summer-2025-ovas' 'summer' 2025 'ovas' $ovas
Write-Page 'summer-2025-all' 'summer' 2025 'all' $all.ToArray()

$winter = @(New-ShowList -Count 20 -Seed 80 -IdBase 940000 -Format 'tv')
Set-Ratings $winter 5
Write-Page 'winter-2025-tv' 'winter' 2025 'tv' $winter

# A positively verified empty season: valid page, chart container and sort control present, no show cards.
Write-Page 'fall-2027-movies' 'fall' 2027 'movies' @()
