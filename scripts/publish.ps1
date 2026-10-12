param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$odyleRoot = Split-Path $PSScriptRoot -Parent
$packageKind = if ($SelfContained) { 'self-contained' } else { 'small' }
# A fresh staging folder prevents stale runtime files from inflating a package.
$output = Join-Path $odyleRoot ('artifacts/staging/' + $packageKind + '-' + $Runtime + '-' + [guid]::NewGuid().ToString('N'))
$bundledRuntime = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $odyleRoot 'src/Odyle.Reader/Odyle.Reader.csproj') -c Release -r $Runtime --self-contained $bundledRuntime -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item -LiteralPath (Join-Path $odyleRoot 'THIRD-PARTY.md') -Destination $output
$suffix = if ($SelfContained) { '-self-contained' } else { '' }
$archive = Join-Path $odyleRoot "artifacts/odyle-$Runtime$suffix.zip"
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $(Split-Path $archive -Leaf)"
Write-Output ("Created {0} ({1:N2} MB)" -f $archive, ((Get-Item -LiteralPath $archive).Length / 1MB))
if (!$SelfContained) { Write-Output 'Small package requires Microsoft .NET 10 Desktop Runtime. Use -SelfContained to bundle it.' }
