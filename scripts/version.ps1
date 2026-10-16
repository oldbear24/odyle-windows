# Semantic versions (AGENTS.md, Versioning): <Version> in src/Odyle.Reader/Odyle.Reader.csproj is
# MAJOR.MINOR.PATCH, bumped once per change by what it changes, and a release is tagged with exactly it.
#
#   ./scripts/version.ps1 check [-Base <ref>]   a pull request or branch: when it changes what ships,
#                                               its version must be higher than the base's (default:
#                                               where it left origin/main), and CHANGELOG.md must
#                                               describe it
#   ./scripts/version.ps1 release               the release workflow: writes version= and release= to
#                                               $GITHUB_OUTPUT. No release when nothing that ships changed
#                                               since the latest release; a failure when shipped code
#                                               changed without a version above every earlier release
param([Parameter(Mandatory)][ValidateSet('check', 'release')][string]$Mode, [string]$Base = '')
$ErrorActionPreference = 'Stop'
$project = 'src/Odyle.Reader/Odyle.Reader.csproj'
function VersionOf([xml]$xml) { ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1) }
function Fail([string]$message) { Write-Host "::error::$message"; exit 1 }
# Earlier releases were tagged MAJOR.MINOR.PATCH.RUN; missing parts count as zero.
function Parts([string]$text) { $p = @($text.TrimStart('v').Split('.') | ForEach-Object { [int]$_ }); while ($p.Count -lt 4) { $p += 0 }; $p }
function Compare-Version([string]$a, [string]$b) { $x = Parts $a; $y = Parts $b; for ($i = 0; $i -lt 4; $i++) { if ($x[$i] -ne $y[$i]) { return $x[$i] - $y[$i] } }; 0 }
function SortKey([string]$tag) { $p = Parts $tag; '{0:D6}{1:D6}{2:D6}{3:D6}' -f $p[0], $p[1], $p[2], $p[3] }
# What does not ship: documentation, tests, CI and this script (packaging scripts do ship).
function Shipped([string[]]$files) { , @($files | Where-Object { $_ -and $_ -notmatch '^(docs/|tests/|\.github/|\.vscode/)|\.md$|^scripts/(version|changelog)\.ps1$|^(LICENSE|\.gitignore|\.gitattributes|\.editorconfig)$' }) }
function List([string[]]$files) { (($files | Select-Object -First 5) -join ', ') + $(if ($files.Count -gt 5) { ', …' } else { '' }) }

$version = VersionOf ([xml](Get-Content -Raw $project))
if ($version -notmatch '^\d+\.\d+\.\d+$') { Fail "<Version> in $project is ""$version""; it must be MAJOR.MINOR.PATCH (AGENTS.md, Versioning)." }
if ($Mode -eq 'check') {
    if (!$Base) { $Base = git merge-base HEAD origin/main }
    $before = VersionOf ([xml](git show "${Base}:$project" | Out-String))
    $changed = Shipped @(git diff --name-only $Base HEAD)
    if ($changed.Count -eq 0) { Write-Host "Nothing that ships changed; the version stays $version."; exit 0 }
    if ((Compare-Version $version $before) -le 0) { Fail "$($changed.Count) shipped file(s) changed ($(List $changed)) but the version is still $version. Bump <Version> in $project by what changed: MAJOR for a breaking change, MINOR for a feature, PATCH for a fix (AGENTS.md, Versioning)." }
    # A new version says what it changed, for players (CHANGELOG.md).
    & (Join-Path $PSScriptRoot 'changelog.ps1') check -Version $version
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "Version $before -> $version for $($changed.Count) shipped file(s), described in CHANGELOG.md."
    exit 0
}
$tags = @(git tag --list 'v*' | Where-Object { $_ -match '^v\d+(\.\d+){1,3}$' } | Sort-Object { SortKey $_ })
$latest = if ($tags.Count) { $tags[-1] } else { '' }
$changed = if ($latest) { Shipped @(git diff --name-only $latest HEAD) } else { , @('(no release yet)') }
$release = 'true'
if ($changed.Count -eq 0) {
    Write-Host "Nothing that ships changed since ${latest}: no release."
    $release = 'false'
} elseif ($tags -contains "v$version") {
    Fail "v$version is already released and $($changed.Count) shipped file(s) changed since the latest release $latest ($(List $changed)). Bump <Version> in $project (AGENTS.md, Versioning)."
} elseif ($latest -and (Compare-Version $version $latest) -le 0) {
    Fail "$version is not above the latest release $latest; installed apps would not take it as newer."
}
$out = "version=$version`nrelease=$release"
if ($env:GITHUB_OUTPUT) { Add-Content -Path $env:GITHUB_OUTPUT -Value $out } else { Write-Host $out }
