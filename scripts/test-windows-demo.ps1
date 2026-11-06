param(
    [string]$PlayerPath,
    [string]$OutputDirectory,
    [switch]$SoftwareRendering,
    [ValidateRange(30, 900)][int]$TimeoutSeconds = 360
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PlayerPath) { $PlayerPath = Join-Path $repoRoot 'unity\Builds\Windows\BrainwashSandbox.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot '.local\ci\windows-smoke' }
if (-not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) { throw "Windows Player is missing: $PlayerPath" }
$PlayerPath = (Resolve-Path -LiteralPath $PlayerPath).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
    throw "Choose an empty -OutputDirectory so all smoke evidence belongs to this run: $OutputDirectory"
}
$reportPath = Join-Path $OutputDirectory 'demo-smoke.json'
$logPath = Join-Path $OutputDirectory 'Player.log'
if (Test-Path -LiteralPath $reportPath) { throw "A prior report exists. Choose a fresh -OutputDirectory: $reportPath" }
$arguments = @(
    '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '640',
    '-brainwash-demo-smoke', ('"' + $reportPath + '"'), '-logFile', ('"' + $logPath + '"')
)
if ($SoftwareRendering) { $arguments += @('-force-d3d11', '-force-driver-type-warp') }
# This exercises rendering and screenshots. Do not add -nographics or -batchmode.
$player = Start-Process -FilePath $PlayerPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
try {
    if (-not $player.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $player.Id -Force
        throw "Windows demo smoke timed out after $TimeoutSeconds seconds. See $logPath"
    }
    $player.Refresh()
    if ($player.ExitCode -ne 0) { throw "Windows demo smoke exited $($player.ExitCode). See $logPath" }
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw 'Player exited without a smoke report.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $checks = @($report.checks)
    if ($report.passed -ne $true -or $checks.Count -eq 0 -or @($checks | Where-Object { $_.pass -ne $true }).Count) {
        throw "Windows demo reported a failure. See $reportPath"
    }
    foreach ($requiredDemo in @('pit_arena', 'puppet_war', 'ledge_key', 'rite_of_return', 'eye_heist', 'gallery', 'menu')) {
        if (@($checks | Where-Object { $_.demo -eq $requiredDemo }).Count -eq 0) { throw "Missing scenario evidence: $requiredDemo" }
    }
    & (Join-Path $PSScriptRoot 'verify-smoke-images.ps1') -ReportPath $reportPath
    Write-Host "Passed $($checks.Count) packaged-game checks. Report: $reportPath"
} finally {
    if (-not $player.HasExited) { Stop-Process -Id $player.Id -Force }
    $player.Dispose()
}
