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

# Compose normally names containers `<project>-<service>-1`. Pin stable names for this
# single-instance deployment so logs and scripts can refer to the service directly.
$composeFile = Join-Path $OutputPath 'docker-compose.yaml'
$composeLines = Get-Content -LiteralPath $composeFile
$inServices = $false
$inTopLevelSection = $false
$result = foreach ($line in $composeLines) {
    if ($line -eq 'services:') {
        $inServices = $true
        $line
        continue
    }

    if ($inServices -and $line -match '^[^ ]' -and $line -ne 'services:') {
        $inTopLevelSection = $true
        $inServices = $false
    }

    if ($inServices -and $line -match '^  ([A-Za-z0-9][A-Za-z0-9_-]*):$') {
        $serviceName = $Matches[1]
        $line
        "    container_name: froststream-$serviceName"
        continue
    }

    $line
}
Set-Content -LiteralPath $composeFile -Value $result
}
finally {
    Pop-Location
}
