param([switch]$SkipPortable)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$projectPath = Join-Path $PSScriptRoot 'src\ClipNest\ClipNest.csproj'
& (Join-Path $PSScriptRoot 'scripts\Convert-AppIcon.ps1')
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
if ($SkipPortable) { return }
$outputPath = Join-Path $PSScriptRoot 'artifacts\Release\win-x64'
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $outputPath -Force
Write-Output (Join-Path $outputPath "ClipNest.exe")
