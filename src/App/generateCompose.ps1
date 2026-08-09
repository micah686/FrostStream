param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'docker-compose-artifacts')
)

$ErrorActionPreference = 'Stop'
$appHostProject = Join-Path $PSScriptRoot 'AppHost/AppHost.csproj'
$sourceEnv = Join-Path $PSScriptRoot 'AppHost/aspire-development.env'

if (-not (Test-Path -LiteralPath $appHostProject)) { throw "AppHost project not found: $appHostProject" }
if (-not (Test-Path -LiteralPath $sourceEnv)) { throw "Environment file not found: $sourceEnv" }
if (-not (Get-Command aspire -ErrorAction SilentlyContinue)) { throw 'Aspire CLI not found on PATH.' }
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

function Publish-ComposeVariant([bool]$Development, [string]$ComposeName, [string]$EnvName) {
    $tempEnv = Join-Path ([IO.Path]::GetTempPath()) ("froststream-compose-{0}.env" -f [guid]::NewGuid())
    $envLines = Get-Content -LiteralPath $sourceEnv
    $modeLine = if ($Development) { 'FROSTSTREAM_DEV_TOOLS="true"' } else { 'FROSTSTREAM_DEV_TOOLS="false"' }
    $envLines = $envLines | Where-Object { $_ -notmatch '^FROSTSTREAM_DEV_TOOLS=' }
    $envLines += $modeLine
    Set-Content -LiteralPath $tempEnv -Value $envLines

    $previousEnvFile = $env:FROSTSTREAM_ENV_FILE
    $previousDevTools = $env:FROSTSTREAM_DEV_TOOLS
    try {
        $env:FROSTSTREAM_ENV_FILE = $tempEnv
        $env:FROSTSTREAM_DEV_TOOLS = if ($Development) { 'true' } else { 'false' }
        Push-Location $PSScriptRoot
        try {
            Remove-Item -LiteralPath (Join-Path $OutputPath '.env') -Force -ErrorAction SilentlyContinue
            & aspire publish --apphost $appHostProject -o $OutputPath --non-interactive --nologo
            if ($LASTEXITCODE -ne 0) { throw "aspire publish failed with exit code $LASTEXITCODE" }
        } finally { Pop-Location }
    } finally {
        if ($null -eq $previousEnvFile) { Remove-Item Env:FROSTSTREAM_ENV_FILE -ErrorAction SilentlyContinue }
        else { $env:FROSTSTREAM_ENV_FILE = $previousEnvFile }
        if ($null -eq $previousDevTools) { Remove-Item Env:FROSTSTREAM_DEV_TOOLS -ErrorAction SilentlyContinue }
        else { $env:FROSTSTREAM_DEV_TOOLS = $previousDevTools }
        Remove-Item -LiteralPath $tempEnv -Force -ErrorAction SilentlyContinue
    }

    $composeFile = Join-Path $OutputPath 'docker-compose.yaml'
    $targetCompose = Join-Path $OutputPath $ComposeName
    $targetEnv = Join-Path $OutputPath $EnvName
    $lines = Get-Content -LiteralPath $composeFile
    $inServices = $false
    $result = foreach ($line in $lines) {
        if ($line -eq 'services:') { $inServices = $true; $line; continue }
    if ($inServices -and $line -match '^[^ ]') { $inServices = $false }
        if ($inServices -and $line -match '^  ([A-Za-z0-9][A-Za-z0-9_-]*):$') {
            $name = $Matches[1]
            $line
            "    container_name: froststream-$name"
            continue
        }
        $line
    }
    if (-not $Development) {
        $yaml = $result -join [Environment]::NewLine
        $yaml = [regex]::Replace($yaml, '(?ms)^  aspire-docker-demo-dashboard:\r?\n.*?(?=^  [A-Za-z0-9][A-Za-z0-9_-]*:|^networks:)', '')
        $result = $yaml -split '\r?\n'
    }
    Set-Content -LiteralPath $targetCompose -Value $result
    if (([IO.Path]::GetFullPath((Join-Path $OutputPath '.env'))) -ne ([IO.Path]::GetFullPath($targetEnv))) {
        Copy-Item -LiteralPath (Join-Path $OutputPath '.env') -Destination $targetEnv -Force
    }
}

Publish-ComposeVariant $true 'docker-compose-dev.yaml' '.env-dev'
Publish-ComposeVariant $false 'docker-compose.yaml' '.env'
