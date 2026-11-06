param(
    [string]$UnityPath = $env:UNITY_PATH,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
if (-not $UnityPath) {
    $candidates = @(
        'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe'
    )
    $UnityPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $UnityPath -or -not (Test-Path -LiteralPath $UnityPath)) { throw 'Install Unity 6000.3.26f1 with Windows Mono support in Unity Hub, or pass -UnityPath.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $repoRoot '.local\unity'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory 'build.log'
$method = if ($PrepareOnly) { 'Brainwash.Editor.SandboxBuild.Prepare' } else { 'Brainwash.Editor.SandboxBuild.BuildWindows' }
$arguments = @('-batchmode', '-quit', '-projectPath', ('"' + $PSScriptRoot + '"'), '-executeMethod', $method, '-logFile', ('"' + $logPath + '"'))
$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
Get-Content -LiteralPath $logPath -Tail 30
if ($process.ExitCode -ne 0) { throw "Unity exited $($process.ExitCode). See $logPath. Sign in and activate your license in Unity Hub if licensing failed." }
Write-Host "Unity completed successfully. Log: $logPath"
