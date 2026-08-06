param(
    [Parameter(Mandatory = $false)]
    [string]$CoverageRoot = ".\artifacts\testresults"
)

$ErrorActionPreference = "Stop"

function ConvertTo-NormalizedPathText {
    param([string]$PathText)

    if ([string]::IsNullOrWhiteSpace($PathText)) {
        return ""
    }

    return $PathText.Replace('\\', '/').Replace('//', '/').Trim()
}

function Get-FileCoveragePercent {
    param(
        [System.Xml.XmlNodeList]$ClassNodes,
        [string]$FilePathSuffix
    )

    $normalizedSuffix = ConvertTo-NormalizedPathText -PathText $FilePathSuffix
    $normalizedLeafName = ConvertTo-NormalizedPathText -PathText (Split-Path -Path $normalizedSuffix -Leaf)
    $lineHitsByNumber = @{}

    foreach ($classNode in $ClassNodes) {
        $classFileName = ConvertTo-NormalizedPathText -PathText $classNode.filename
        $matchesSuffix = $classFileName.EndsWith($normalizedSuffix, [System.StringComparison]::OrdinalIgnoreCase)
        $matchesLeaf = $classFileName.EndsWith($normalizedLeafName, [System.StringComparison]::OrdinalIgnoreCase)
        if (-not ($matchesSuffix -or $matchesLeaf)) {
            continue
        }

        $lineNodes = @($classNode.SelectNodes("lines/line"))
        foreach ($lineNode in $lineNodes) {
            $lineNumber = [int]$lineNode.number
            $lineHits = [int]$lineNode.hits

            if ($lineHitsByNumber.ContainsKey($lineNumber)) {
                if ($lineHits -gt $lineHitsByNumber[$lineNumber]) {
                    $lineHitsByNumber[$lineNumber] = $lineHits
                }
            }
            else {
                $lineHitsByNumber[$lineNumber] = $lineHits
            }
        }
    }

    if ($lineHitsByNumber.Count -eq 0) {
        return $null
    }

    $covered = ($lineHitsByNumber.Values | Where-Object { $_ -gt 0 }).Count
    return [math]::Round((100.0 * $covered) / $lineHitsByNumber.Count, 1)
}

$coverageFile = Get-ChildItem -Path $CoverageRoot -Recurse -Filter "*.cobertura.xml" -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($null -eq $coverageFile) {
    throw "Coverage file '*.cobertura.xml' not found under '$CoverageRoot'."
}

[xml]$coverageXml = Get-Content -Path $coverageFile.FullName -Raw
$classNodes = $coverageXml.GetElementsByTagName("class")
if ($null -eq $classNodes -or $classNodes.Count -eq 0) {
    throw "No class nodes found in coverage file '$($coverageFile.FullName)'."
}

$rules = @(
    @{ Component = "execution";     File = "src/Tsky.Desktop/TaskExecutionService.cs";   Minimum = 60.0 },
    @{ Component = "dispatch";      File = "src/Tsky.Desktop/RemoteControlPlaneService.cs"; Minimum = 5.0 },
    @{ Component = "scheduler";     File = "src/Tsky.Desktop/SchedulerTimingService.cs"; Minimum = 75.0 },
    @{ Component = "orchestration"; File = "src/Tsky.Desktop/MainWindow.xaml.cs";       Minimum = 30.0 }
)

$results = foreach ($rule in $rules) {
    $actual = Get-FileCoveragePercent -ClassNodes $classNodes -FilePathSuffix $rule.File
    if ($null -eq $actual) {
        [pscustomobject]@{
            Component = $rule.Component
            File      = $rule.File
            Minimum   = $rule.Minimum
            Actual    = "missing"
            Status    = "Fail"
        }
        continue
    }

    [pscustomobject]@{
        Component = $rule.Component
        File      = $rule.File
        Minimum   = $rule.Minimum
        Actual    = $actual
        Status    = if ($actual -ge $rule.Minimum) { "Pass" } else { "Fail" }
    }
}

Write-Host "Coverage source: $($coverageFile.FullName)"
$results | Format-Table -AutoSize

$failed = @($results | Where-Object { $_.Status -eq "Fail" })
if ($failed.Count -gt 0) {
    $details = $failed | ForEach-Object {
        "$($_.Component): actual=$($_.Actual), minimum=$($_.Minimum), file=$($_.File)"
    }

    throw "Coverage thresholds failed:`n$($details -join "`n")"
}

Write-Host "All critical component coverage thresholds passed."
