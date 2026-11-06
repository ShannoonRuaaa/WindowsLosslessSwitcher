[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$DotNetPath,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
if (-not $DotNetPath) {
    $localSdk = Join-Path (Split-Path $PSScriptRoot -Parent) '.dotnet/dotnet.exe'
    $DotNetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotNetPath = (Get-Command $DotNetPath -ErrorAction Stop).Source
if (-not (& $DotNetPath --list-sdks)) {
    throw 'Install the .NET 8 SDK, or pass -DotNetPath with the path to an SDK dotnet.exe.'
}

$previousRoot = $env:DOTNET_ROOT
$env:DOTNET_ROOT = Split-Path $DotNetPath -Parent
$outputDirectory = Join-Path $PSScriptRoot "artifacts/publish/$Runtime"
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        & $DotNetPath test ./WindowsLosslessSwitcher.sln -c Release --logger 'trx;LogFileName=regression.trx' --results-directory ./artifacts/test-results
        if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed; publish cancelled.' }
    }
    & $DotNetPath publish ./src/WindowsLosslessSwitcher.csproj -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=false `
        -p:WlsAppVersion=1.0.0-intro.10 -p:WlsAssemblyVersion=1.0.0.10 -p:WlsEnableUpdates=false `
        -o $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
    if ($Runtime -eq 'win-x64') {
        & $DotNetPath run --project ./tests/WindowsLosslessSwitcher.UiVerification/WindowsLosslessSwitcher.UiVerification.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Release UI verification failed.' }
    }
    Copy-Item -LiteralPath ./LICENSE, ./THIRD_PARTY_NOTICES.md, ./OPTIMIZATION_NOTES.md -Destination $outputDirectory
    Write-Host "Built: $(Join-Path $outputDirectory 'WindowsLosslessSwitcher.exe')"
}
finally {
    Pop-Location
    $env:DOTNET_ROOT = $previousRoot
}
