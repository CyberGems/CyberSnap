# Refreshes the About icons before release builds. All sources must pass validation;
# unavailable or invalid sources never fall back to possibly outdated cached icons.
[CmdletBinding()]
param(
    [ValidateSet('GitHub', 'Local')][string]$Source = 'GitHub',
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$ProjectsRoot,
    [string]$ConfigPath = '.github/about-icons.json',
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
function Resolve-RepositoryPath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Expected a repository-relative path: $RelativePath"
    }
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $RelativePath))
    $rootPrefix = $RepositoryRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (!$fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        @($RelativePath -split '[/\\]' | Where-Object { $_ -in @('artifacts', 'AGENTS.md', '.git', '.agents', '.codex') }).Count -gt 0) {
        throw "Path is outside the allowed repository files: $RelativePath"
    }
    return $fullPath
}

$config = Get-Content -LiteralPath (Resolve-RepositoryPath $ConfigPath) -Raw | ConvertFrom-Json
$targetDirectories = @($config.destinations | ForEach-Object { Resolve-RepositoryPath $_ } | Select-Object -Unique)
if ($targetDirectories.Count -eq 0) { throw 'No About icon destinations are configured.' }
$catalogSlugs = @()
foreach ($rule in $config.catalogs) {
    $catalog = [System.IO.File]::ReadAllText((Resolve-RepositoryPath $rule.path))
    if ($rule.PSObject.Properties.Name -contains 'start') {
        $start = $catalog.IndexOf($rule.start, [System.StringComparison]::Ordinal)
        if ($start -lt 0) { throw "Catalog start marker is missing in $($rule.path)." }
        $end = $catalog.IndexOf($rule.end, $start + $rule.start.Length, [System.StringComparison]::Ordinal)
        if ($end -lt 0) { throw "Catalog end marker is missing in $($rule.path)." }
        $catalog = $catalog.Substring($start, $end - $start)
    }
    $matches = [regex]::Matches($catalog, $rule.pattern, [System.Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(2))
    if ($matches.Count -eq 0) { throw "No About apps matched the catalog rule in $($rule.path)." }
    foreach ($match in $matches) {
        $slug = $match.Groups[1].Value
        if ($slug -notmatch '^cyber[a-z0-9-]+$') { throw "Invalid About catalog slug in $($rule.path): $slug" }
        $catalogSlugs += $slug
    }
}
$catalogSlugs = @($catalogSlugs | Sort-Object -Unique)
if ($config.PSObject.Properties.Name -contains 'exclude') {
    foreach ($excluded in $config.exclude) {
        if ($excluded -notin $catalogSlugs) { throw "Excluded app is not present in the About catalog: $excluded" }
    }
    $catalogSlugs = @($catalogSlugs | Where-Object { $_ -notin $config.exclude })
}
$registry = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SuiteIconSources.json') -Raw | ConvertFrom-Json)
if ($catalogSlugs.Count -eq 0 -or $registry.Count -eq 0) { throw 'The About catalog or icon sources are empty.' }
if (@($registry.slug | Select-Object -Unique).Count -ne $registry.Count) { throw 'Icon sources must not contain duplicate apps.' }
$unmapped = @($catalogSlugs | Where-Object { $_ -notin $registry.slug })
if ($unmapped.Count -gt 0) { throw "Missing canonical icon sources for About apps: $($unmapped -join ', ')." }
foreach ($entry in $registry) {
    if ($entry.slug -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or
        $entry.repo -notmatch '^Cyber[A-Za-z0-9]+$' -or
        $entry.ref -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        $entry.path -notmatch '^[A-Za-z0-9_/-][A-Za-z0-9_./-]*\.png$' -or
        [System.IO.Path]::IsPathRooted($entry.path) -or
        @($entry.path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw "Invalid About icon source: $($entry.slug)."
    }
}
$sources = @($registry | Where-Object { $_.slug -in $catalogSlugs })

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
        $iconChanged = $false
        foreach ($targetDirectory in $targetDirectories) {
            $target = Join-Path $targetDirectory "$($entry.slug).png"
            $unchanged = (Test-Path -LiteralPath $target -PathType Leaf) -and
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $metadata.Sha256
            $pending += [pscustomobject]@{ Slug = $entry.slug; Bytes = $bytes; Target = $target; Changed = !$unchanged }
            $iconChanged = $iconChanged -or !$unchanged
        }
        $report += [pscustomobject]@{
            slug = $entry.slug; source = $origin; sha256 = $metadata.Sha256
            width = $metadata.Width; height = $metadata.Height; changed = $iconChanged
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
    foreach ($directory in $targetDirectories) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
    foreach ($icon in $changed) { [System.IO.File]::WriteAllBytes($icon.Target, $icon.Bytes) }
}
if ($ReportPath) {
    $reportFullPath = [System.IO.Path]::GetFullPath($ReportPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($reportFullPath)) | Out-Null
    [System.IO.File]::WriteAllText($reportFullPath, (ConvertTo-Json -InputObject $report -Depth 3) + "`n",
        [System.Text.UTF8Encoding]::new($false))
}
Write-Output "About icons: $($sources.Count) apps validated, $($changed.Count) files updated, $($pending.Count - $changed.Count) files unchanged (check=$Check)."
