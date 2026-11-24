param(
    [string]$DotnetExe = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
    [string]$OutputDir = "$PSScriptRoot\..\publish\win-x64",
    [switch]$SelfContained = $true,
    [switch]$SingleFile = $false
)

if (-not (Test-Path $DotnetExe)) {
    $DotnetExe = "dotnet"
}

$HostProj = Join-Path $PSScriptRoot "..\src\SignerBridge.Host\SignerBridge.Host.csproj"
$sc = if ($SelfContained) { "true" } else { "false" }
$sf = if ($SingleFile) { "true" } else { "false" }

Write-Host "Publishing SignerBridge.Host (win-x64, SelfContained=$sc, SingleFile=$sf) -> $OutputDir"
& $DotnetExe publish $HostProj `
    -c Release `
    -r win-x64 `
    --self-contained $sc `
    -p:PublishSingleFile=$sf `
    -p:IncludeNativeLibrariesForSelfExtract=$sf `
    -p:EnableCompressionInSingleFile=$sf `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publish completed successfully."
