param([switch]$Publish, [switch]$Run)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$islandSdk = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:LOCALAPPDATA 'IslandBuild\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $islandSdk)) { throw 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and run this script again.' }
$project = Join-Path $PSScriptRoot 'src\Island\Island.csproj'
if ($Publish) {
    $destination = Join-Path $PSScriptRoot 'dist\Island'
    & $islandSdk publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $destination
} else { & $islandSdk build $project -c Release }
if ($LASTEXITCODE -ne 0) { throw 'Island build failed.' }
if ($Run) {
    if ($Publish) { Start-Process -FilePath (Join-Path $destination 'Island.exe') -ArgumentList '--open' -WindowStyle Hidden }
    else { & $islandSdk run --project $project -c Release --no-build -- --open }
}
