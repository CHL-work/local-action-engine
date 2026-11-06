param(
    [string]$PythonPath,
    [string]$ModelArchive,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$node = Get-Command node.exe -ErrorAction SilentlyContinue
if (-not $node) { throw 'Install Node.js 22.13+ from https://nodejs.org/ and reopen this terminal.' }
$nodeVersion = & $node.Source -p 'process.versions.node'
if ($LASTEXITCODE -ne 0 -or [version]$nodeVersion -lt [version]'22.13.0') { throw 'Node.js 22.13+ is required.' }
$pythonProbeFailures = [System.Collections.Generic.List[string]]::new()
function Find-Python312 {
    param([string]$Executable, [string[]]$PrefixArguments = @())
    try {
        # Windows PowerShell 5.1 turns redirected native stderr into an error
        # record. A missing py-selected version is an expected discovery miss.
        $probe = & $Executable @PrefixArguments -c 'import sys; print(sys.executable) if sys.version_info[:2] == (3,12) and sys.maxsize > 2**32 else sys.exit(1)' 2>&1
        if ($LASTEXITCODE -eq 0 -and $probe) {
            $path = ($probe | Select-Object -Last 1).ToString().Trim()
            if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
        }
        $detail = ($probe | ForEach-Object { $_.ToString() }) -join ' '
        if (-not $detail) { $detail = 'not Python 3.12 x64' }
        $pythonProbeFailures.Add($Executable + ': ' + $detail)
    } catch {
        $pythonProbeFailures.Add($Executable + ': ' + $_.Exception.Message)
    }
    return $null
}
if (-not $PythonPath) {
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        $PythonPath = Find-Python312 -Executable $launcher.Source -PrefixArguments @('-3.12')
    }
    if (-not $PythonPath) {
        foreach ($name in @('python.exe', 'python3.exe')) {
            $candidate = Get-Command $name -ErrorAction SilentlyContinue
            if ($candidate) {
                $PythonPath = Find-Python312 -Executable $candidate.Source
                if ($PythonPath) { break }
            }
        }
    }
}
if (-not $PythonPath -or -not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
    $detail = if ($pythonProbeFailures.Count) { ' Discovery: ' + ($pythonProbeFailures -join '; ') } else { '' }
    throw ('Install Python 3.12 x64 from python.org, or pass -PythonPath to its python.exe. See docs/windows-setup.md.' + $detail)
}
$PythonPath = (Resolve-Path -LiteralPath $PythonPath).Path
$arguments = @((Join-Path $repoRoot 'tools\setup_runtime.py'), '--root', $repoRoot)
if ($ModelArchive) { $arguments += @('--model-archive', (Resolve-Path -LiteralPath $ModelArchive).Path) }
if ($VerifyOnly) { $arguments += '--verify-only' }
Push-Location -LiteralPath $repoRoot
try {
    if (-not $VerifyOnly) {
        & npm.cmd ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Locked Node dependency installation failed.' }
    }
    $nodeCheck = @'
import {createRequire} from 'node:module';
const require = createRequire(import.meta.url);
const actual = require('ajv/package.json').version;
if (actual !== '8.17.1') throw new Error('Expected ajv 8.17.1; found ' + actual);
await import('./training/contract.mjs');
console.log('Node runtime dependencies verified.');
'@
    & $node.Source --input-type=module -e $nodeCheck
    if ($LASTEXITCODE -ne 0) { throw 'Node runtime dependencies are missing or mismatched. Run Setup Demo.cmd to restore the lockfile versions.' }
    & $PythonPath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Runtime setup failed. See the error above.' }
} finally { Pop-Location }
