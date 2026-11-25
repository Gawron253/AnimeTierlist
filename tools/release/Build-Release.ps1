<#
.SYNOPSIS
Builds the portable Windows release: out/AnimeTierlist-<version>-win-x64.zip plus a SHA-256 checksum file.

.DESCRIPTION
Publishes the self-contained win-x64 desktop app in Release with locked package restore, strips debug symbols and
local build paths, adds README.md, LICENSE, THIRD_PARTY_NOTICES.md, and upstream third-party license files under
licenses/, zips the folder, and writes "<hash>  <file>" to a .sha256 file next to the ZIP. Nothing is uploaded.
Run from any directory with the pinned .NET SDK on PATH.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectDir = Join-Path $root 'src\AnimeTierlist.Desktop'
$project = Join-Path $projectDir 'AnimeTierlist.Desktop.csproj'
$version = (Select-Xml -Path $project -XPath '//Version').Node.InnerText
if (-not $version) { throw 'The desktop project has no <Version>.' }

$name = "AnimeTierlist-$version-win-x64"
$out = Join-Path $root 'out'
$stage = Join-Path $out $name
$zip = Join-Path $out "$name.zip"
foreach ($path in @($stage, $zip, "$zip.sha256")) { if (Test-Path $path) { Remove-Item $path -Recurse -Force } }

Push-Location $root
try {
    dotnet publish $project -c Release -o $stage `
        -p:RestoreLockedMode=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:ContinuousIntegrationBuild=true `
        "-p:PathMap=$root=/"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

if (Test-Path (Join-Path $stage 'data')) { throw 'The publish folder must not contain a data folder.' }

Copy-Item (Join-Path $root 'README.md') (Join-Path $stage 'README.md')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE')
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') (Join-Path $stage 'THIRD_PARTY_NOTICES.md')

$msLicDest = Join-Path (Join-Path $stage 'licenses') 'microsoft'
New-Item -ItemType Directory -Path $msLicDest -Force | Out-Null
foreach ($msLic in @('MICROSOFT_DOTNET_LIBRARY_LICENSE.txt', 'WINDOWS_SDK_LICENSE.txt')) {
    $msLicSrc = Join-Path (Join-Path $root 'docs\licenses') $msLic
    if (-not (Test-Path $msLicSrc)) { throw "Missing required Microsoft license file: $msLicSrc" }
    Copy-Item -LiteralPath $msLicSrc -Destination (Join-Path $msLicDest $msLic)
}

# Collect upstream license and third-party notice files from the resolved NuGet packages and runtime packs.
$assetsPath = Join-Path $projectDir 'obj\project.assets.json'
$depsPath = Join-Path $stage 'AnimeTierlist.deps.json'
$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
$deps = Get-Content $depsPath -Raw | ConvertFrom-Json

$packageRoots = New-Object System.Collections.Generic.List[string]
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    if (Test-Path $folder) { $packageRoots.Add($folder) }
}
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCmd) {
    $packsDir = Join-Path (Split-Path -Parent $dotnetCmd.Source) 'packs'
    if (Test-Path $packsDir) { $packageRoots.Add($packsDir) }
}
if ($env:DOTNET_ROOT) {
    $rootPacks = Join-Path $env:DOTNET_ROOT 'packs'
    if ((Test-Path $rootPacks) -and (-not $packageRoots.Contains($rootPacks))) { $packageRoots.Add($rootPacks) }
}

function Copy-PackageNotices {
    param(
        [string]$PackageId,
        [string]$PackageVersion,
        [string]$Subfolder,
        [string[]]$FileNames
    )
    $pkgDir = $null
    foreach ($base in $packageRoots) {
        foreach ($candidate in @(
            (Join-Path $base (Join-Path $PackageId.ToLowerInvariant() $PackageVersion)),
            (Join-Path $base (Join-Path $PackageId $PackageVersion))
        )) {
            if (Test-Path $candidate) { $pkgDir = $candidate; break }
        }
        if ($pkgDir) { break }
    }
    if (-not $pkgDir) { throw "Could not find package directory for $PackageId $PackageVersion." }

    $destDir = Join-Path (Join-Path $stage 'licenses') $Subfolder
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    foreach ($fileName in $FileNames) {
        $srcFile = Join-Path $pkgDir $fileName
        if (-not (Test-Path $srcFile)) { throw "Missing required notice file '$fileName' in $pkgDir." }
        Copy-Item -LiteralPath $srcFile -Destination (Join-Path $destDir $fileName)
    }
}

$resolvedVersions = @{}
foreach ($prop in $deps.libraries.PSObject.Properties) {
    $parts = $prop.Name -split '/'
    if ($parts.Length -eq 2) {
        $id = $parts[0] -replace '^runtimepack\.', ''
        $resolvedVersions[$id.ToLowerInvariant()] = $parts[1]
    }
}

$requiredNotices = @(
    @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Folder = 'dotnet-runtime'; Files = @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') },
    @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Folder = 'windowsdesktop-runtime'; Files = @('LICENSE') },
    @{ Id = 'SkiaSharp.NativeAssets.Win32'; Folder = 'SkiaSharp'; Files = @('LICENSE.txt', 'THIRD-PARTY-NOTICES.txt') },
    @{ Id = 'Microsoft.Web.WebView2'; Folder = 'Microsoft.Web.WebView2'; Files = @('LICENSE.txt', 'NOTICE.txt') },
    @{ Id = 'CommunityToolkit.Mvvm'; Folder = 'CommunityToolkit.Mvvm'; Files = @('License.md', 'ThirdPartyNotices.txt') }
)
foreach ($entry in $requiredNotices) {
    $ver = $resolvedVersions[$entry.Id.ToLowerInvariant()]
    if (-not $ver) { throw "Could not determine resolved version for $($entry.Id) in $depsPath." }
    Copy-PackageNotices -PackageId $entry.Id -PackageVersion $ver -Subfolder $entry.Folder -FileNames $entry.Files
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline

$files = Get-ChildItem $stage -Recurse -File
Write-Output ("{0}: {1:N1} MB in {2} files; zip {3:N1} MB; sha256 {4}" -f $name, (($files | Measure-Object Length -Sum).Sum / 1MB), $files.Count, ((Get-Item $zip).Length / 1MB), $hash)