param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$odyleRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $odyleRoot "artifacts/$Runtime"
dotnet publish (Join-Path $odyleRoot 'src/Odyle.Reader/Odyle.Reader.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
$archive = Join-Path $odyleRoot "artifacts/odyle-$Runtime.zip"
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $(Split-Path $archive -Leaf)"
Write-Output "Created $archive"
