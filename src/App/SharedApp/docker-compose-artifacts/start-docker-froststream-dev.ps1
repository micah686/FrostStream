param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ComposeArguments
)

$ErrorActionPreference = 'Stop'
$composeFile = Join-Path $PSScriptRoot 'docker-compose-dev.yaml'
$envFile = Join-Path $PSScriptRoot '.env-dev'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI not found on PATH.'
}
if (-not (Test-Path -LiteralPath $composeFile)) {
    throw "Development Compose file not found: $composeFile. Run src/App/generateCompose.ps1 first."
}
if (-not (Test-Path -LiteralPath $envFile)) {
    throw "Development environment file not found: $envFile. Run src/App/generateCompose.ps1 first."
}

& docker compose --env-file $envFile -f $composeFile up -d --build @ComposeArguments
exit $LASTEXITCODE
