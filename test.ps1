param([switch]$Motion, [switch]$Lifetime)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$islandSdk = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:LOCALAPPDATA 'IslandBuild\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $islandSdk)) { throw 'Install the .NET 10 SDK first.' }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
& $islandSdk run --project (Join-Path $PSScriptRoot 'tests\Island.Tests\Island.Tests.csproj') -c Release -- (Join-Path $artifacts 'test-results.json')
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
$interactionAssembly = Join-Path $PSScriptRoot 'src\Island\bin\Release\net10.0-windows10.0.19041.0\Island.dll'
$interactionReport = Join-Path $artifacts 'hover-report.json'
$interactionArguments = '"' + $interactionAssembly + '" --interaction-check --report "' + $interactionReport + '"'
$interaction = Start-Process -FilePath $islandSdk -ArgumentList $interactionArguments -WindowStyle Hidden -PassThru -Wait
if ($interaction.ExitCode -ne 0) { throw "Window hover checks failed. Read $interactionReport" }
Get-Content -LiteralPath $interactionReport
if ($Lifetime) {
    & (Join-Path $PSScriptRoot 'tests\Check-ProcessLifetime.ps1') -Executable (Join-Path $PSScriptRoot 'dist\Island\Island.exe') -Report (Join-Path $artifacts 'lifetime-after.json')
    & (Join-Path $PSScriptRoot 'tests\Check-ProcessLifetime.ps1') -Executable (Join-Path $PSScriptRoot 'dist\Island\Island.exe') -Report (Join-Path $artifacts 'lifetime-nested-after.json') -Nested
}
if ($Motion) {
    $executable = Join-Path $PSScriptRoot 'dist\Island\Island.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'Run .\build.ps1 -Publish first to verify the distribution build.' }
    $arguments = '--motion-check --report "' + (Join-Path $artifacts 'motion-report.json') + '"'
    $check = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($check.ExitCode -ne 0) { throw "Motion/interaction checks failed. Read $artifacts\motion-report.json" }
    Get-Content -LiteralPath (Join-Path $artifacts 'motion-report.json')
}
