param(
    [Parameter(Mandatory = $false)]
    [string]$BaselinePath = ".\eng\analyzers\baseline.json",

    [Parameter(Mandatory = $false)]
    [string]$CurrentSarifPath = ".\artifacts\analyzers\current.sarif",

    [Parameter(Mandatory = $false)]
    [string]$RepoRoot = ".",

    [Parameter(Mandatory = $false)]
    [switch]$UpdateBaseline
)

$ErrorActionPreference = "Stop"

function ConvertTo-NormalizedRepoPath {
    param(
        [string]$UriText,
        [string]$ResolvedRepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($UriText)) {
        return ""
    }

    $path = $UriText.Trim()
    if ($path.StartsWith("file:///", [System.StringComparison]::OrdinalIgnoreCase)) {
        $path = $path.Substring(8)
    }
    elseif ($path.StartsWith("file://", [System.StringComparison]::OrdinalIgnoreCase)) {
        $path = $path.Substring(7)
    }

    $path = [System.Uri]::UnescapeDataString($path)
    $path = $path.Replace('\', '/')

    if ([System.IO.Path]::IsPathRooted($path)) {
        $repoRootNormalized = $ResolvedRepoRoot.Replace('\', '/').TrimEnd('/')
        $pathNormalized = $path.Replace('\', '/').TrimEnd('/')

        if (-not $pathNormalized.StartsWith($repoRootNormalized, [System.StringComparison]::OrdinalIgnoreCase)) {
            return ""
        }

        $relativeStartIndex = $repoRootNormalized.Length
        if ($pathNormalized.Length -gt $relativeStartIndex) {
            $path = $pathNormalized.Substring($relativeStartIndex)
        }
        else {
            $path = ""
        }
    }

    while ($path.StartsWith("./", [System.StringComparison]::Ordinal)) {
        $path = $path.Substring(2)
    }

    while ($path.StartsWith("/", [System.StringComparison]::Ordinal)) {
        $path = $path.Substring(1)
    }

    return $path
}

function Get-SarifDiagnosticEntries {
    param(
        [string]$SarifPath,
        [string]$ResolvedRepoRoot
    )

    if (-not (Test-Path -Path $SarifPath -PathType Leaf)) {
        throw "SARIF file '$SarifPath' does not exist."
    }

    $json = Get-Content -Path $SarifPath -Raw
    $sarif = $json | ConvertFrom-Json -Depth 100
    $entries = New-Object System.Collections.Generic.List[object]

    foreach ($run in @($sarif.runs)) {
        foreach ($result in @($run.results)) {
            $ruleId = [string]$result.ruleId
            if ([string]::IsNullOrWhiteSpace($ruleId)) {
                continue
            }

            $location = $null
            if ($null -ne $result.locations -and $result.locations.Count -gt 0) {
                $location = $result.locations[0]
            }

            if ($null -eq $location) {
                continue
            }

            $uri = ""
            $startLine = 0
            $startColumn = 0

            if ($null -ne $location.resultFile) {
                $uri = [string]$location.resultFile.uri
                if ($null -ne $location.resultFile.region) {
                    $startLine = [int]($location.resultFile.region.startLine ?? 0)
                    $startColumn = [int]($location.resultFile.region.startColumn ?? 0)
                }
            }
            elseif ($null -ne $location.physicalLocation) {
                if ($null -ne $location.physicalLocation.artifactLocation) {
                    $uri = [string]$location.physicalLocation.artifactLocation.uri
                }

                if ($null -ne $location.physicalLocation.region) {
                    $startLine = [int]($location.physicalLocation.region.startLine ?? 0)
                    $startColumn = [int]($location.physicalLocation.region.startColumn ?? 0)
                }
            }

            $relativePath = ConvertTo-NormalizedRepoPath -UriText $uri -ResolvedRepoRoot $ResolvedRepoRoot
            if ([string]::IsNullOrWhiteSpace($relativePath)) {
                continue
            }

            if ($relativePath -like "obj/*" -or
                $relativePath -like "bin/*" -or
                $relativePath -like "*/obj/*" -or
                $relativePath -like "*/bin/*") {
                continue
            }

            $fingerprint = "{0}|{1}|{2}|{3}" -f $ruleId, $relativePath.ToLowerInvariant(), $startLine, $startColumn

            $entries.Add([pscustomobject]@{
                ruleId      = $ruleId
                path        = $relativePath
                line        = $startLine
                column      = $startColumn
                fingerprint = $fingerprint
            })
        }
    }

    return @($entries | Sort-Object fingerprint -Unique)
}

$resolvedRepoRoot = (Resolve-Path -Path $RepoRoot).Path
$currentEntries = Get-SarifDiagnosticEntries -SarifPath $CurrentSarifPath -ResolvedRepoRoot $resolvedRepoRoot

if ($UpdateBaseline) {
    $baselineDirectory = Split-Path -Path $BaselinePath -Parent
    if (-not [string]::IsNullOrWhiteSpace($baselineDirectory)) {
        New-Item -Path $baselineDirectory -ItemType Directory -Force | Out-Null
    }

    $payload = [ordered]@{
        schemaVersion  = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        sourceSarif    = (Resolve-Path -Path $CurrentSarifPath).Path
        entryCount     = $currentEntries.Count
        entries        = @($currentEntries | Select-Object ruleId, path, line, column, fingerprint)
    }

    $payload | ConvertTo-Json -Depth 8 | Set-Content -Path $BaselinePath -Encoding UTF8
    Write-Host "Analyzer/style baseline updated: $BaselinePath ($($currentEntries.Count) entries)."
    exit 0
}

if (-not (Test-Path -Path $BaselinePath -PathType Leaf)) {
    throw "Baseline file '$BaselinePath' not found. Run with -UpdateBaseline first."
}

$baseline = Get-Content -Path $BaselinePath -Raw | ConvertFrom-Json -Depth 100
$baselineSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($baseline.entries)) {
    if ($null -ne $entry.fingerprint -and -not [string]::IsNullOrWhiteSpace([string]$entry.fingerprint)) {
        [void]$baselineSet.Add([string]$entry.fingerprint)
    }
}

$newEntries = @()
foreach ($entry in $currentEntries) {
    if (-not $baselineSet.Contains($entry.fingerprint)) {
        $newEntries += $entry
    }
}

Write-Host "Analyzer/style baseline entries: $($baselineSet.Count)"
Write-Host "Current analyzer/style entries: $($currentEntries.Count)"

if ($newEntries.Count -gt 0) {
    Write-Host "New diagnostics not in baseline: $($newEntries.Count)"
    $newEntries |
        Sort-Object ruleId, path, line, column |
        Select-Object -First 200 ruleId, path, line, column |
        Format-Table -AutoSize

    $summary = $newEntries |
        Group-Object ruleId |
        Sort-Object Count -Descending |
        ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }

    throw "Analyzer/style baseline check failed. New diagnostics by rule:`n$($summary -join "`n")"
}

Write-Host "Analyzer/style baseline check passed. No new diagnostics introduced."
