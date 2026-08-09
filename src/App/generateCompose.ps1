param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'docker-compose-artifacts')
)

$ErrorActionPreference = 'Stop'

$appHostProject = Join-Path $PSScriptRoot 'AppHost/AppHost.csproj'
if (-not (Test-Path -LiteralPath $appHostProject)) {
    throw "AppHost project not found: $appHostProject"
}

if (-not (Get-Command aspire -ErrorAction SilentlyContinue)) {
    throw 'Aspire CLI not found on PATH. Install the Aspire CLI before running generateCompose.'
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

Push-Location $PSScriptRoot
try {
    & aspire publish --apphost $appHostProject -o $OutputPath --non-interactive --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "aspire publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
