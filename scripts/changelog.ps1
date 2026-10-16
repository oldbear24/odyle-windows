# The changelog (CHANGELOG.md), as src/Odyle.Core/Changelog.cs reads it, for the checks and the
# release workflow:
#
#   ./scripts/changelog.ps1 check -Version <x.y.z>   the version's section is there, lists something
#                                                    and uses only New, Changed and Fixed
#   ./scripts/changelog.ps1 notes -Version <x.y.z>   prints that section as release notes, between the
#                                                    markers the dashboard reads them back from
param([Parameter(Mandatory)][ValidateSet('check', 'notes')][string]$Mode, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
function Fail([string]$message) { Write-Host "::error::$message"; exit 1 }
$lines = (Get-Content -Raw -Encoding utf8 (Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md')) -split "`r?`n"
$found = $false; $inside = $false; $groups = [ordered]@{}; $group = $null
foreach ($line in $lines) {
    if ($line -match '^## (\d+\.\d+\.\d+)(?:\s*·\s*\d{4}-\d{2}-\d{2})?\s*$') { if ($inside) { break }; $inside = $Matches[1] -eq $Version; $found = $found -or $inside; continue }
    if (!$inside) { continue }
    if ($line -match '^### (.+)$') { $group = $Matches[1].Trim(); $groups[$group] = [System.Collections.Generic.List[string]]::new(); continue }
    if ($group -and $line -match '^- (.+)$') { $groups[$group].Add($Matches[1].Trim()); continue }
    if ($group -and $groups[$group].Count -and $line -match '^\s+\S') { $groups[$group][$groups[$group].Count - 1] += ' ' + $line.Trim() }
}
$listed = @($groups.Keys | Where-Object { $groups[$_].Count -gt 0 })
if (!$found) { Fail "CHANGELOG.md has no section for $Version. Add ""## $Version · <date>"" with what changed, for players (AGENTS.md, Versioning)." }
if ($listed.Count -eq 0) { Fail "CHANGELOG.md's section for $Version lists nothing. Add ""### New"", ""### Changed"" or ""### Fixed"" with ""- "" entries." }
$odd = $listed | Where-Object { $_ -notin @('New', 'Changed', 'Fixed') } | Select-Object -First 1
if ($odd) { Fail "CHANGELOG.md's section for $Version has a group ""$odd""; use New, Changed or Fixed." }
if ($Mode -eq 'check') { Write-Host "CHANGELOG.md describes $Version."; exit 0 }
$notes = @('<!-- changes -->') + @($listed | ForEach-Object { "### $_"; $groups[$_] | ForEach-Object { "- $_" }; '' }) + @('<!-- /changes -->')
$notes -join "`n"
