# Refreshes the About icons before release builds. All sources must pass validation;
# unavailable or invalid sources never fall back to possibly outdated cached icons.
[CmdletBinding()]
param(
    [ValidateSet('GitHub', 'Local')][string]$Source = 'GitHub',
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$ProjectsRoot,
    [switch]$Check,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Net.Http

function Get-IconMetadata([byte[]]$Bytes, [string]$Label) {
    if ($Bytes.Length -lt 24 -or $Bytes.Length -gt 5MB) {
        throw "${Label}: expected a PNG between 24 bytes and 5 MB."
    }
    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    for ($i = 0; $i -lt $signature.Count; $i++) {
        if ($Bytes[$i] -ne $signature[$i]) { throw "${Label}: the source is not a PNG." }
    }
    $stream = [System.IO.MemoryStream]::new($Bytes, $false)
    $image = $null
    try {
        $image = [System.Drawing.Image]::FromStream($stream, $false, $true)
        if ($image.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid) {
            throw "${Label}: the source did not decode as PNG."
        }
        if ($image.Width -ne $image.Height -or $image.Width -lt 128 -or $image.Width -gt 4096) {
            throw "${Label}: expected a square icon from 128x128 to 4096x4096, got $($image.Width)x$($image.Height)."
        }
        $stream.Position = 0
        return [pscustomobject]@{
            Width = $image.Width
            Height = $image.Height
            Sha256 = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    finally {
        if ($null -ne $image) { $image.Dispose() }
        $stream.Dispose()
    }
}

$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
if (!$ProjectsRoot) { $ProjectsRoot = Split-Path $RepositoryRoot -Parent }
$ProjectsRoot = [System.IO.Path]::GetFullPath($ProjectsRoot)
$catalogPath = Join-Path $RepositoryRoot 'src/CyberSnap/UI/SuiteCatalog.cs'
$targetDirectory = Join-Path $RepositoryRoot 'src/CyberSnap/Assets/Suite'
$sources = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SuiteIconSources.json') -Raw | ConvertFrom-Json)
$catalog = [System.IO.File]::ReadAllText($catalogPath)
$catalogSlugs = @([regex]::Matches($catalog, 'new\("([a-z0-9-]+)",\s*"Cyber[A-Za-z0-9]+"') |
    ForEach-Object { $_.Groups[1].Value })
if ($catalogSlugs.Count -eq 0 -or $sources.Count -eq 0) { throw 'The About catalog or icon sources are empty.' }
if (@($catalogSlugs | Select-Object -Unique).Count -ne $catalogSlugs.Count -or
    @($sources.slug | Select-Object -Unique).Count -ne $sources.Count) {
    throw 'The About catalog and icon sources must not contain duplicate apps.'
}
if (@(Compare-Object ($catalogSlugs | Sort-Object) ($sources.slug | Sort-Object)).Count -ne 0) {
    throw 'SuiteIconSources.json must contain exactly the apps in SuiteCatalog.cs. Update both when adding an app.'
}
foreach ($entry in $sources) {
    if ($entry.slug -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or
        $entry.repo -notmatch '^Cyber[A-Za-z0-9]+$' -or
        $entry.ref -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        $entry.path -notmatch '^[A-Za-z0-9_/-][A-Za-z0-9_./-]*\.png$' -or
        [System.IO.Path]::IsPathRooted($entry.path) -or
        @($entry.path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw "Invalid About icon source: $($entry.slug)."
    }
}

# Keep the complete validated batch in memory. No cached icon is touched until
# every source has downloaded, decoded, and passed the size/coverage checks.
$pending = @()
$report = @()
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(20)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('CyberSnap-Suite-Icons/1.0')
try {
    foreach ($entry in $sources) {
        $url = 'https://raw.githubusercontent.com/CyberGems/{0}/{1}/{2}' -f
            $entry.repo, $entry.ref, (($entry.path.Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
        if ($Source -eq 'Local') {
            $origin = Join-Path (Join-Path $ProjectsRoot $entry.repo) $entry.path
            $bytes = [System.IO.File]::ReadAllBytes($origin)
        }
        else {
            $origin = $url
            try { $bytes = $http.GetByteArrayAsync($url).GetAwaiter().GetResult() }
            catch { throw "Could not download $($entry.slug) from ${url}: $($_.Exception.Message)" }
        }
        $metadata = Get-IconMetadata $bytes $entry.slug
        $target = Join-Path $targetDirectory "$($entry.slug).png"
        $unchanged = (Test-Path -LiteralPath $target -PathType Leaf) -and
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $metadata.Sha256
        $pending += [pscustomobject]@{ Slug = $entry.slug; Bytes = $bytes; Target = $target; Changed = !$unchanged }
        $report += [pscustomobject]@{
            slug = $entry.slug; source = $origin; sha256 = $metadata.Sha256
            width = $metadata.Width; height = $metadata.Height; changed = !$unchanged
        }
        Write-Output "$($entry.slug): validated $($metadata.Width)x$($metadata.Height), SHA256 $($metadata.Sha256), source $origin"
    }
}
finally { $http.Dispose() }

$changed = @($pending | Where-Object Changed)
if ($Check -and $changed.Count -gt 0) {
    throw "Outdated About icons: $($changed.Slug -join ', '). Run scripts/Sync-SuiteIcons.ps1 before building."
}
if (!$Check) {
    [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
    foreach ($icon in $changed) { [System.IO.File]::WriteAllBytes($icon.Target, $icon.Bytes) }
}
if ($ReportPath) {
    $reportFullPath = [System.IO.Path]::GetFullPath($ReportPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($reportFullPath)) | Out-Null
    [System.IO.File]::WriteAllText($reportFullPath, (ConvertTo-Json -InputObject $report -Depth 3) + "`n",
        [System.Text.UTF8Encoding]::new($false))
}
Write-Output "About icons: $($sources.Count) validated, $($changed.Count) updated, $($sources.Count - $changed.Count) unchanged (check=$Check)."
