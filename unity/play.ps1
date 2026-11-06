param(
    [string]$LocalRoot,
    [ValidateRange(1024,65535)][int]$Port = 4174,
    [switch]$Build,
    [switch]$Setup,
    [string]$UnityPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $LocalRoot) { $LocalRoot = $repoRoot }
$LocalRoot = (Resolve-Path -LiteralPath $LocalRoot).Path
if ($Setup -and $LocalRoot -ne $repoRoot) {
    throw '-Setup prepares this checkout only. Omit -LocalRoot when setting up.'
}
if ($Build) { & (Join-Path $PSScriptRoot 'build.ps1') -UnityPath $UnityPath }
$player = Join-Path $PSScriptRoot 'Builds\Windows\BrainwashSandbox.exe'
if (-not (Test-Path -LiteralPath $player)) { throw 'Windows build missing. Run unity/build.ps1 (after Unity license activation), or unity/play.ps1 -Build.' }
if ($Setup) { & (Join-Path $repoRoot 'setup.ps1') }
foreach ($required in @(
    (Join-Path $LocalRoot '.local\laya-env\Scripts\python.exe'),
    (Join-Path $LocalRoot '.local\prototype-english-stt-env\Scripts\python.exe'),
    (Join-Path $LocalRoot '.local\models\brainwash-en-v3-opus55-2026-10-13\epoch-04\model.safetensors'),
    (Join-Path $LocalRoot '.local\models\stt\parakeet-unified-en-0.6b-fp32-e69c9fb\prototype-model.json'),
    (Join-Path $repoRoot 'node_modules\ajv\package.json')
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Run Setup Demo.cmd first. Missing: $required" }
}
$logDirectory = Join-Path $repoRoot '.local\unity'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$health = $null
try { $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/health" -TimeoutSec 2 } catch {}
if ($health -and $health.service -ne 'brainwash-unity-inference-v1') { throw "Port $Port belongs to another service. Choose -Port." }
if (-not $health) {
    $service = Join-Path $repoRoot 'prototype\start-unity-service.ps1'
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $service + '"'),'-Port',[string]$Port)
    if ($LocalRoot) { $arguments += @('-LocalRoot', ('"' + $LocalRoot + '"')) }
    $helper = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logDirectory 'service.log') -RedirectStandardError (Join-Path $logDirectory 'service-error.log')
    $helper.Id | Set-Content -LiteralPath (Join-Path $logDirectory 'service-launcher.pid')
}
# The game displays loading/offline status; model startup does not freeze its UI.
Start-Process -FilePath $player -ArgumentList @('-screen-fullscreen','0','-screen-width','1600','-screen-height','1000','-brainwash-port',[string]$Port,'-logFile',('"' + (Join-Path $logDirectory 'player.log') + '"'))
Write-Host "Sandbox opened. Service logs: $logDirectory"
Write-Host 'Stop the inference helper later with: ./prototype/start-unity-service.ps1 -Stop'
