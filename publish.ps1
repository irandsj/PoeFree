# Builds release packages into ./publish
#   PoeFree.exe  - GUI (single file, self contained)
#   poefree-cli.exe - command line (single file, self contained)
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "publish\$Runtime"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$common = @("-c", "Release", "-r", $Runtime, "--self-contained", "true",
  "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
  "-p:DebugType=none", "-p:EnableCompressionInSingleFile=true", "-o", $out)

dotnet publish "$PSScriptRoot\src\PoeFree.App\PoeFree.App.csproj" @common
dotnet publish "$PSScriptRoot\src\PoeFree.Cli\PoeFree.Cli.csproj" @common

Copy-Item "$PSScriptRoot\LICENSE" $out
Copy-Item "$PSScriptRoot\README.md" $out
Write-Host "Done: $out"
Write-Host "Note: oo2core.dll is NOT included (proprietary). PoeFree downloads/locates it on first start."
