# Behavioral checks use an isolated local fixture and never contact GitHub or
# modify the real About assets. No test framework or additional packages needed.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path $PSScriptRoot -Parent
$sync = Join-Path $PSScriptRoot 'Sync-SuiteIcons.ps1'
$sources = @(Get-Content (Join-Path $PSScriptRoot 'SuiteIconSources.json') -Raw | ConvertFrom-Json)
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('CyberSnap-SuiteIcons-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $temp 'CyberSnap'
$targets = Join-Path $fixture 'src/CyberSnap/Assets/Suite'
$catalog = Join-Path $fixture 'src/CyberSnap/UI/SuiteCatalog.cs'
$checks = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "FAIL: $Message" }
    $script:checks++
    Write-Output "PASS: $Message"
}

function Assert-Rejected([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    $failure = $null
    try { & $Action | Out-Null }
    catch { $failure = $_.Exception.Message }
    Assert-True ($null -ne $failure -and $failure -match $Pattern) "$Message (error=$failure)"
}

function Sync-Fixture([switch]$Check) {
    & $sync -Source Local -RepositoryRoot $fixture -ProjectsRoot $temp -Check:$Check | Out-Null
}

function Get-TargetSnapshot {
    return (@(Get-ChildItem -LiteralPath $targets -File | Sort-Object Name | ForEach-Object {
        "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName).Hash):$($_.LastWriteTimeUtc.Ticks)"
    }) -join "`n")
}

try {
    [System.IO.Directory]::CreateDirectory($targets) | Out-Null
    [System.IO.Directory]::CreateDirectory((Split-Path $catalog -Parent)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'src/CyberSnap/UI/SuiteCatalog.cs') -Destination $catalog
    foreach ($entry in $sources) {
        $canonical = Join-Path (Join-Path $temp $entry.repo) $entry.path
        [System.IO.Directory]::CreateDirectory((Split-Path $canonical -Parent)) | Out-Null
        $asset = Join-Path $repo "src/CyberSnap/Assets/Suite/$($entry.slug).png"
        Copy-Item -LiteralPath $asset -Destination $canonical
        Copy-Item -LiteralPath $asset -Destination (Join-Path $targets "$($entry.slug).png")
    }
    $baseline = Get-TargetSnapshot
    Sync-Fixture -Check
    Sync-Fixture
    Assert-True ((Get-TargetSnapshot) -eq $baseline) 'matching sources and check mode preserve bytes and timestamps'

    $first = Join-Path (Join-Path $temp $sources[0].repo) $sources[0].path
    $second = Join-Path (Join-Path $temp $sources[1].repo) $sources[1].path
    $firstOriginal = [System.IO.File]::ReadAllBytes($first)
    $secondOriginal = [System.IO.File]::ReadAllBytes($second)
    [System.IO.File]::WriteAllBytes($first, $secondOriginal)
    Assert-Rejected { Sync-Fixture -Check } 'Outdated About icons' 'check mode reports an outdated icon'
    Assert-True ((Get-TargetSnapshot) -eq $baseline) 'an outdated check does not modify cached icons'

    [System.IO.File]::WriteAllText($second, '<html>Not an image</html>')
    Assert-Rejected { Sync-Fixture } 'not a PNG' 'an invalid later source is rejected'
    Assert-True ((Get-TargetSnapshot) -eq $baseline) 'a failed batch leaves all icons intact, including earlier valid updates'

    $truncated = [byte[]]::new(24)
    [byte[]]$testSignature = @(137, 80, 78, 71, 13, 10, 26, 10)
    $testSignature.CopyTo($truncated, 0)
    [System.IO.File]::WriteAllBytes($second, $truncated)
    Assert-Rejected { Sync-Fixture } 'FromStream' 'a truncated PNG with the correct signature cannot pass decoding'
    Assert-True ((Get-TargetSnapshot) -eq $baseline) 'a decoding failure preserves cached images'

    $rectangle = [System.Drawing.Bitmap]::new(128, 256)
    try { $rectangle.Save($second, [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $rectangle.Dispose() }
    Assert-Rejected { Sync-Fixture } 'square icon' 'non-square images are rejected'

    $small = [System.Drawing.Bitmap]::new(32, 32)
    try { $small.Save($second, [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $small.Dispose() }
    Assert-Rejected { Sync-Fixture } '128x128' 'images too small for high-DPI display are rejected'

    [System.IO.File]::WriteAllBytes($second, [byte[]]::new(5MB + 1))
    Assert-Rejected { Sync-Fixture } '5 MB' 'oversized files are rejected'
    Assert-True ((Get-TargetSnapshot) -eq $baseline) 'validation failures never apply pending valid updates'
    [System.IO.File]::WriteAllBytes($second, $secondOriginal)

    [System.IO.File]::Delete($second)
    Assert-Rejected { Sync-Fixture } 'ReadAllBytes' 'an unavailable source fails instead of using the cached icon'
    Assert-True ((Get-TargetSnapshot) -eq $baseline) 'an unavailable later source preserves the entire cached batch'
    [System.IO.File]::WriteAllBytes($second, $secondOriginal)

    $report = Join-Path $temp 'report.json'
    & $sync -Source Local -RepositoryRoot $fixture -ProjectsRoot $temp -ReportPath $report | Out-Null
    $firstTarget = Join-Path $targets "$($sources[0].slug).png"
    Assert-True ((Get-FileHash $firstTarget).Hash -eq (Get-FileHash $first).Hash) 'sync copies a changed canonical PNG byte for byte'
    $entries = @(Get-Content $report -Raw | ConvertFrom-Json)
    Assert-True ($entries.Count -eq $sources.Count -and @($entries | Where-Object changed).Count -eq 1 -and
        @($entries | Where-Object { $_.sha256 -notmatch '^[a-f0-9]{64}$' }).Count -eq 0) 'the report covers every app with hashes and the single updated icon'
    $synced = Get-TargetSnapshot
    Sync-Fixture
    Sync-Fixture -Check
    Assert-True ((Get-TargetSnapshot) -eq $synced) 'running sync again is idempotent'

    [System.IO.File]::WriteAllBytes($first, $firstOriginal)
    [System.IO.File]::WriteAllText($catalog, ([System.IO.File]::ReadAllText($catalog) -replace
        [regex]::Escape("new(`"$($sources[0].slug)`""), 'new("unmapped-app"'))
    $beforeMismatch = Get-TargetSnapshot
    Assert-Rejected { Sync-Fixture } 'exactly the apps' 'catalog/source mismatches stop the sync'
    Assert-True ((Get-TargetSnapshot) -eq $beforeMismatch) 'a missing source mapping cannot partially update assets'
    Write-Output "PASS: $checks About icon synchronization checks."
}
finally {
    # Delete only this fixture, created under the resolved system temp directory.
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $resolvedFixture = [System.IO.Path]::GetFullPath($temp)
    if (!$resolvedFixture.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedFixture) -notmatch '^CyberSnap-SuiteIcons-[a-f0-9]{32}$') {
        throw 'Refusing to remove a fixture outside the expected temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
