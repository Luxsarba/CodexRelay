$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$assembly = Join-Path $projectDir 'bin\Debug\net8.0\CodexRelay.dll'
$logDir = Join-Path $projectDir 'logs'

Set-Location -LiteralPath $projectDir
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

if (-not (Test-Path -LiteralPath $assembly)) {
    throw "Build the project first: dotnet build '$projectDir\CodexRelay.csproj'"
}

& 'C:\Program Files\dotnet\dotnet.exe' $assembly *>> (Join-Path $logDir 'relay.log')
exit $LASTEXITCODE
