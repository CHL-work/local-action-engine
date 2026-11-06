param(
    [string]$LocalRoot,
    [ValidateRange(1024, 65535)][int]$Port = 4174,
    [switch]$Setup,
    [switch]$Stop,
    [string]$NodePath = $env:BRAINWASH_NODE_PATH
)
$ErrorActionPreference = 'Stop'
if ($Stop) {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/health" -TimeoutSec 5
    if ($health.service -ne 'brainwash-unity-inference-v1') { throw 'The port belongs to another service; left untouched.' }
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/shutdown" -Method Post -ContentType 'application/json' -Headers @{'X-Brainwash-Unity'='1'} -Body '{}' -TimeoutSec 10 | Out-Null
    Write-Host 'Local inference service is stopping cleanly.'
    return
}
$sourceRoot = Split-Path -Parent $PSScriptRoot
if (-not $LocalRoot) { $LocalRoot = $sourceRoot }
$LocalRoot = (Resolve-Path -LiteralPath $LocalRoot).Path
if ($Setup) {
    if ($LocalRoot -ne $sourceRoot) { throw '-Setup prepares this checkout only. Omit -LocalRoot when setting up.' }
    & (Join-Path $sourceRoot 'setup.ps1')
}
if (-not $NodePath) {
    $command = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($command) { $NodePath = $command.Source }
    elseif (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'nodejs\node.exe')) {
        $NodePath = Join-Path $env:ProgramFiles 'nodejs\node.exe'
    }
}
if (-not $NodePath -or -not (Test-Path -LiteralPath $NodePath -PathType Leaf)) { throw 'Install Node.js 22.13+ or pass -NodePath.' }
$nodeVersion = & $NodePath -p 'process.versions.node'
if ($LASTEXITCODE -ne 0 -or [version]$nodeVersion -lt [version]'22.13.0') { throw 'Node.js 22.13+ is required.' }
$layaPython = Join-Path $LocalRoot '.local\laya-env\Scripts\python.exe'
$checkpoint = Join-Path $LocalRoot '.local\models\brainwash-en-v3-opus55-2026-10-13\epoch-04'
$sttPython = Join-Path $LocalRoot '.local\prototype-english-stt-env\Scripts\python.exe'
$sttModel = Join-Path $LocalRoot '.local\models\stt\parakeet-unified-en-0.6b-fp32-e69c9fb'
foreach ($required in @($layaPython, $sttPython, (Join-Path $checkpoint 'model.safetensors'), (Join-Path $sttModel 'prototype-model.json'), (Join-Path $sourceRoot 'node_modules\ajv\package.json'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Runtime file missing: $required. Run Setup Demo.cmd first." }
}
Push-Location -LiteralPath $sourceRoot
try {
    & $NodePath --input-type=module -e "await import('./training/contract.mjs')"
    if ($LASTEXITCODE -ne 0) { throw 'Node dependencies failed to load. Run Setup Demo.cmd.' }
    $env:BRAINWASH_LOCAL_ROOT = $LocalRoot
    $env:BRAINWASH_CHECKPOINT = $checkpoint
    $env:BRAINWASH_PYTHON = $layaPython
    $env:BRAINWASH_STT_PYTHON = $sttPython
    $env:BRAINWASH_STT_MODEL = $sttModel
    $env:BRAINWASH_UNITY_PORT = [string]$Port
    Write-Host "Local inference: http://127.0.0.1:$Port (original English epoch-04 and CPU Parakeet)"
    & $NodePath (Join-Path $PSScriptRoot 'unity-server.mjs')
    if ($LASTEXITCODE -ne 0) { throw "Inference service exited with code $LASTEXITCODE" }
} finally { Pop-Location }
