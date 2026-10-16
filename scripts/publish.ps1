param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$SelfContained, [switch]$Installer, [string]$Version)
$ErrorActionPreference = 'Stop'
$odyleRoot = Split-Path $PSScriptRoot -Parent
if ($Installer -and (!$SelfContained -or $Runtime -ne 'win-x64')) { throw '-Installer packages the self-contained win-x64 build: use it with -SelfContained.' }
$packageKind = if ($SelfContained) { 'self-contained' } else { 'small' }
# A fresh staging folder prevents stale runtime files from inflating a package.
$output = Join-Path $odyleRoot ('artifacts/staging/' + $packageKind + '-' + $Runtime + '-' + [guid]::NewGuid().ToString('N'))
# The flag forms: some .NET 10 SDKs read "--self-contained false" as the bare flag and bundle the runtime anyway.
$runtimeKind = if ($SelfContained) { '--self-contained' } else { '--no-self-contained' }
# Release builds stamp their version into the executable and the archive name, and are marked as
# releases (OdyleRelease), which update themselves.
$versionArgs = @(); if ($Version) { $versionArgs += "-p:Version=$Version"; $versionArgs += '-p:OdyleRelease=true' }
dotnet publish (Join-Path $odyleRoot 'src/Odyle.Reader/Odyle.Reader.csproj') -c Release -r $Runtime $runtimeKind -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false @versionArgs -o $output
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item -LiteralPath (Join-Path $odyleRoot 'THIRD-PARTY.md') -Destination $output
# A release package must update itself: the built Odyle says what it is (--build-info). Only x64
# builds run on the build machine; arm64 ones are built from the same flags.
if ($Version -and $Runtime -eq 'win-x64' -and [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') {
  $info = Join-Path ([System.IO.Path]::GetTempPath()) ('odyle-build-' + [guid]::NewGuid().ToString('N') + '.json')
  $probe = Start-Process -FilePath (Join-Path $output 'Odyle.exe') -ArgumentList @('--build-info', "`"$info`"") -Wait -PassThru
  if (!(Test-Path -LiteralPath $info)) { throw "The built Odyle did not say what it is (exit code $($probe.ExitCode))." }
  $built = Get-Content -LiteralPath $info -Raw | ConvertFrom-Json; Remove-Item -LiteralPath $info
  if (-not $built.release -or $built.version -ne $Version -or [bool]$built.selfContained -ne [bool]$SelfContained) { throw "This package would not update itself: $($built | ConvertTo-Json -Compress)" }
  Write-Output "Odyle $($built.version) ($packageKind $Runtime) is a release build that updates itself."
}
$suffix = if ($SelfContained) { '-self-contained' } else { '' }
$prefix = if ($Version) { "odyle-$Version" } else { 'odyle' }
$artifacts = Join-Path $odyleRoot 'artifacts'
# Every file the release attaches gets a .sha256 beside it: "<hash>  <file name>".
function Write-Checksum([string]$Path) {
  $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath "$Path.sha256" -Value "$hash  $(Split-Path $Path -Leaf)"
  Write-Output ("Created {0} ({1:N2} MB)" -f $Path, ((Get-Item -LiteralPath $Path).Length / 1MB))
}
$archive = Join-Path $artifacts "$prefix-$Runtime$suffix.zip"
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
Write-Checksum $archive
if (!$SelfContained) { Write-Output 'Small package requires Microsoft .NET 10 Desktop Runtime. Use -SelfContained to bundle it.' }
if ($Installer) {
  # Inno Setup 6 compiles installer/odyle.iss; on a build machine without it, Chocolatey installs it.
  function Find-Iscc {
    $command = Get-Command iscc -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
    return $null
  }
  $iscc = Find-Iscc
  if (!$iscc) {
    if (!(Get-Command choco -ErrorAction SilentlyContinue)) { throw 'Inno Setup 6 (iscc) is missing. Install it from https://jrsoftware.org/isinfo.php.' }
    choco install innosetup -y --no-progress
    if ($LASTEXITCODE -ne 0) { throw 'Installing Inno Setup failed.' }
    $iscc = Find-Iscc
    if (!$iscc) { throw 'Inno Setup was installed, but ISCC.exe was not found.' }
  }
  $appVersion = if ($Version) { $Version } else { (Select-Xml -Path (Join-Path $odyleRoot 'src/Odyle.Reader/Odyle.Reader.csproj') -XPath '/Project/PropertyGroup/Version' | Select-Object -First 1).Node.InnerText }
  $setupName = "$prefix-win-x64-setup"
  & $iscc /Qp "/DAppVersion=$appVersion" "/DPublishDir=$([System.IO.Path]::GetFullPath($output))" "/DOutputDir=$([System.IO.Path]::GetFullPath($artifacts))" "/DOutputName=$setupName" (Join-Path $odyleRoot 'installer/odyle.iss')
  if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }
  Write-Checksum (Join-Path $artifacts "$setupName.exe")
}
