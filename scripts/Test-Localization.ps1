[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$LocalizationDirectory,
    [string]$BaselinePath,
    [switch]$UpdateBaseline,
    [switch]$Detailed,
    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $PSScriptRoot '..\src\CyberSnap'
}

if ([string]::IsNullOrWhiteSpace($LocalizationDirectory)) {
    $LocalizationDirectory = Join-Path $SourceDirectory 'Localization'
}

$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$LocalizationDirectory = [IO.Path]::GetFullPath($LocalizationDirectory)
if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $PSScriptRoot 'LocalizationKeyBaseline.json'
}
$BaselinePath = [IO.Path]::GetFullPath($BaselinePath)

if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
    throw "Source directory not found: $SourceDirectory"
}

if (-not (Test-Path -LiteralPath $LocalizationDirectory -PathType Container)) {
    throw "Localization directory not found: $LocalizationDirectory"
}

function New-StringSet {
    # Unary comma prevents PowerShell from enumerating an empty collection into $null.
    return ,[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
}

function Add-MatchesToSet {
    param(
        [Collections.Generic.HashSet[string]]$Set,
        [string]$Text,
        [string]$Pattern,
        [switch]$DecodeXml
    )

    foreach ($match in [Text.RegularExpressions.Regex]::Matches(
        $Text,
        $Pattern,
        [Text.RegularExpressions.RegexOptions]::Singleline)) {
        $value = $match.Groups['value'].Value
        if ($DecodeXml) {
            $value = [Net.WebUtility]::HtmlDecode($value)
        }
        else {
            $value = [Text.RegularExpressions.Regex]::Unescape($value)
        }

        if (-not [string]::IsNullOrWhiteSpace($value)) {
            [void]$Set.Add($value)
        }
    }
}

function Get-Placeholders {
    param([string]$Text)

    $placeholders = [Collections.Generic.List[string]]::new()
    foreach ($match in [Text.RegularExpressions.Regex]::Matches($Text, '(?<!\{)\{\d+(?:,[^}:]+)?(?:\:[^}]+)?\}(?!\})')) {
        $normalized = [Text.RegularExpressions.Regex]::Match($match.Value, '^\{\d+').Value + '}'
        $placeholders.Add($normalized)
    }

    return @($placeholders | Sort-Object -Unique)
}

function Compare-StringArrays {
    param([string[]]$Left, [string[]]$Right)
    return [string]::Join('|', $Left) -ceq [string]::Join('|', $Right)
}

$explicitKeys = New-StringSet
$implicitXamlKeys = New-StringSet
$sourceFiles = Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.xaml' }

foreach ($file in $sourceFiles) {
    $text = [IO.File]::ReadAllText($file.FullName)

    if ($file.Extension -eq '.cs') {
        Add-MatchesToSet $explicitKeys $text 'LocalizationService\.Translate\(\s*"(?<value>(?:[^"\\]|\\.)*)"'
        Add-MatchesToSet $explicitKeys $text 'WindowTitles\.(?:Taskbar|ApplyTaskbar)\([^,\r\n]*,?\s*"(?<value>(?:[^"\\]|\\.)*)"'
    }
    else {
        Add-MatchesToSet $explicitKeys $text 'Services:LocalizationService\.Source(?:Text|Content|Header|ToolTip)\s*=\s*"(?<value>[^"]*)"' -DecodeXml

        # ApplyTo localizes ordinary string properties too. These are candidates rather
        # than deletion evidence: styles, bindings, markup extensions, and generated UI
        # can make static XAML analysis incomplete.
        Add-MatchesToSet $implicitXamlKeys $text '(?:Text|Content|Header|ToolTip|Title)\s*=\s*"(?<value>(?!\{)[^"]+)"' -DecodeXml
    }
}

$localeFiles = @(Get-ChildItem -LiteralPath $LocalizationDirectory -Filter '*.json' -File | Sort-Object Name)
if ($localeFiles.Count -eq 0) {
    throw "No locale JSON files found in $LocalizationDirectory"
}

$locales = [ordered]@{}
$duplicateKeys = [Collections.Generic.List[object]]::new()
$invalidFiles = [Collections.Generic.List[object]]::new()

foreach ($file in $localeFiles) {
    try {
        $jsonText = [IO.File]::ReadAllText($file.FullName)
        $document = [Text.Json.JsonDocument]::Parse($jsonText)
        try {
            if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
                throw 'The JSON root must be an object.'
            }

            $values = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
            foreach ($property in $document.RootElement.EnumerateObject()) {
                if ($values.ContainsKey($property.Name)) {
                    $incomingValue = $property.Value.GetString()
                    $duplicateKeys.Add([pscustomobject]@{
                        Locale = $file.BaseName
                        Key = $property.Name
                        FirstValue = $values[$property.Name]
                        LastValue = $incomingValue
                        Conflicts = $values[$property.Name] -cne $incomingValue
                    })
                    # Match the runtime's effective last-value-wins behavior while
                    # retaining a visible structural error for the duplicate.
                    $values[$property.Name] = $incomingValue
                    continue
                }

                if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                    throw "Value for '$($property.Name)' must be a string."
                }

                $values.Add($property.Name, $property.Value.GetString())
            }
            $locales[$file.BaseName] = $values
        }
        finally {
            $document.Dispose()
        }
    }
    catch {
        $invalidFiles.Add([pscustomobject]@{ Locale = $file.BaseName; Error = $_.Exception.Message })
    }
}

if ($UpdateBaseline) {
    if ($invalidFiles.Count -gt 0 -or $duplicateKeys.Count -gt 0) {
        throw 'Cannot update the protected-key baseline while locale files are invalid or contain duplicate keys.'
    }

    $baselineOutput = [ordered]@{
        schemaVersion = 1
        description = 'Protected localization keys. Removing a listed key is an error; additions do not require regenerating this file.'
        locales = [ordered]@{}
    }
    foreach ($localeName in $locales.Keys) {
        $baselineOutput.locales[$localeName] = @($locales[$localeName].Keys | Sort-Object)
    }

    $baselineJson = $baselineOutput | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($BaselinePath, $baselineJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host "Updated protected-key baseline: $BaselinePath" -ForegroundColor Green
}

$removedProtectedKeys = [Collections.Generic.List[object]]::new()
$baselineError = $null
if (Test-Path -LiteralPath $BaselinePath -PathType Leaf) {
    try {
        $baseline = [IO.File]::ReadAllText($BaselinePath) | ConvertFrom-Json
        if ($baseline.schemaVersion -ne 1 -or $null -eq $baseline.locales) {
            throw 'Unsupported or malformed baseline schema.'
        }

        foreach ($localeProperty in $baseline.locales.PSObject.Properties) {
            $localeName = $localeProperty.Name
            foreach ($protectedKey in $localeProperty.Value) {
                if (-not $locales.Contains($localeName) -or -not $locales[$localeName].ContainsKey([string]$protectedKey)) {
                    $removedProtectedKeys.Add([pscustomobject]@{
                        Locale = $localeName
                        Key = [string]$protectedKey
                    })
                }
            }
        }
    }
    catch {
        $baselineError = $_.Exception.Message
    }
}
else {
    $baselineError = "Protected-key baseline not found: $BaselinePath"
}

$catalog = New-StringSet
foreach ($key in $explicitKeys) { [void]$catalog.Add($key) }
if ($locales.Contains('en')) {
    foreach ($key in $locales['en'].Keys) { [void]$catalog.Add($key) }
}

$coverage = [Collections.Generic.List[object]]::new()
$placeholderProblems = [Collections.Generic.List[object]]::new()
$blankValues = [Collections.Generic.List[object]]::new()
$requiredMissingKeys = [Collections.Generic.List[object]]::new()
$unverifiedKeys = [ordered]@{}

foreach ($localeName in $locales.Keys) {
    $values = $locales[$localeName]
    $missing = @($catalog | Where-Object { -not $values.ContainsKey($_) } | Sort-Object)
    $unverified = @($values.Keys | Where-Object { -not $catalog.Contains($_) } | Sort-Object)
    $unverifiedKeys[$localeName] = $unverified
    if ($localeName -in 'en', 'es') {
        foreach ($missingKey in $missing) {
            $requiredMissingKeys.Add([pscustomobject]@{ Locale = $localeName; Key = $missingKey })
        }
    }

    foreach ($entry in $values.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace($entry.Value)) {
            $blankValues.Add([pscustomobject]@{ Locale = $localeName; Key = $entry.Key })
        }

        $referenceText = if ($locales.Contains('en') -and $locales['en'].ContainsKey($entry.Key)) {
            $locales['en'][$entry.Key]
        }
        else {
            $entry.Key
        }
        $keyPlaceholders = @(Get-Placeholders $referenceText)
        $valuePlaceholders = @(Get-Placeholders $entry.Value)
        if (-not (Compare-StringArrays $keyPlaceholders $valuePlaceholders)) {
            $placeholderProblems.Add([pscustomobject]@{
                Locale = $localeName
                Key = $entry.Key
                Expected = [string]::Join(', ', $keyPlaceholders)
                Actual = [string]::Join(', ', $valuePlaceholders)
            })
        }
    }

    $coverage.Add([pscustomobject]@{
        Locale = $localeName
        Keys = $values.Count
        Missing = $missing.Count
        Unverified = $unverified.Count
        Coverage = if ($catalog.Count -eq 0) { '100.0%' } else { '{0:N1}%' -f ((($catalog.Count - $missing.Count) / $catalog.Count) * 100) }
    })
}

Write-Host "Localization audit (read-only)"
Write-Host "Source:  $SourceDirectory"
Write-Host "Locales: $LocalizationDirectory"
Write-Host "Protected-key baseline: $BaselinePath"
Write-Host "Audit catalog: $($catalog.Count) ($($explicitKeys.Count) explicit source keys plus English catalog entries)"
Write-Host "Implicit XAML candidates tracked separately: $($implicitXamlKeys.Count)"
Write-Host ''
$coverage | Format-Table -AutoSize

if ($invalidFiles.Count -gt 0) {
    Write-Host "`nInvalid locale files:" -ForegroundColor Red
    $invalidFiles | Format-Table -Wrap
}

if ($duplicateKeys.Count -gt 0) {
    Write-Host "`nDuplicate keys:" -ForegroundColor Red
    $duplicateKeys | Format-Table -Wrap
}

if ($null -ne $baselineError) {
    Write-Host "`nProtected-key baseline error:" -ForegroundColor Red
    Write-Host "  $baselineError"
}

if ($removedProtectedKeys.Count -gt 0) {
    Write-Host "`nRemoved protected keys:" -ForegroundColor Red
    $removedProtectedKeys | Format-Table -Wrap
    Write-Host 'Restore these keys. Baseline updates require explicit review and must never be used to hide accidental deletion.'
}

if ($blankValues.Count -gt 0) {
    Write-Host "`nBlank translations:" -ForegroundColor Yellow
    $blankValues | Format-Table -Wrap
}

if ($placeholderProblems.Count -gt 0) {
    Write-Host "`nPlaceholder mismatches:" -ForegroundColor Red
    $placeholderProblems | Format-Table -Wrap
}

if ($requiredMissingKeys.Count -gt 0) {
    Write-Host "`nMissing required English/Spanish keys:" -ForegroundColor Red
    $requiredMissingKeys | Format-Table -Wrap
}

if ($Detailed) {
foreach ($localeName in @('en', 'es')) {
    if (-not $locales.Contains($localeName)) { continue }

    $missing = @($catalog | Where-Object { -not $locales[$localeName].ContainsKey($_) } | Sort-Object)
    if ($missing.Count -gt 0) {
        Write-Host "`n$localeName missing catalog candidates ($($missing.Count)):" -ForegroundColor Yellow
        $missing | ForEach-Object { Write-Host "  $_" }
    }

    $unverified = $unverifiedKeys[$localeName]
    if ($unverified.Count -gt 0) {
        Write-Host "`n$localeName retained/unverified keys ($($unverified.Count)):" -ForegroundColor Cyan
        Write-Host '  These may be dynamic or legacy. The audit never treats them as safe to delete.'
        $unverified | ForEach-Object { Write-Host "  $_" }
    }
}
}

$baselineFailureCount = if ($null -eq $baselineError) { 0 } else { 1 }
$hardFailureCount = $invalidFiles.Count + $duplicateKeys.Count + $blankValues.Count + $placeholderProblems.Count + $requiredMissingKeys.Count + $removedProtectedKeys.Count + $baselineFailureCount
if ($Strict -and $hardFailureCount -gt 0) {
    Write-Error "Localization audit found $hardFailureCount structural error(s). Missing and unverified keys are warnings only."
    exit 1
}

Write-Host "`nStructural errors: $hardFailureCount"
Write-Host 'Missing keys and retained/unverified keys are warnings; neither category authorizes deletion.'
