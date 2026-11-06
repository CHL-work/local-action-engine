param([string]$UnityPath)
$ErrorActionPreference = 'Stop'

if (-not $UnityPath) {
    $candidates = @(
        $env:UNITY_PATH,
        'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe'
    )
    $UnityPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $UnityPath -or -not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw 'Pass -UnityPath pointing to an installed Unity 6000.3.26f1 Editor/Unity.exe. This test does not install software.'
}

$editorData = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $UnityPath).Path) 'Data'
$dotnet = Join-Path $editorData 'NetCoreRuntime\dotnet.exe'
$compiler = Join-Path $editorData 'DotNetSdkRoslyn\csc.dll'
$runtimeConfig = Join-Path $editorData 'DotNetSdkRoslyn\csc.runtimeconfig.json'
$referenceDirectory = Join-Path $editorData 'NetStandard\ref\2.1.0'
$engineDirectory = Join-Path $editorData 'Managed\UnityEngine'
$assemblyNames = @('UnityEngine.CoreModule.dll', 'UnityEngine.AudioModule.dll', 'UnityEngine.InputLegacyModule.dll')
foreach ($required in @($dotnet, $compiler, $runtimeConfig, $referenceDirectory) + ($assemblyNames | ForEach-Object { Join-Path $engineDirectory $_ })) {
    if (-not (Test-Path -LiteralPath $required)) { throw "The installed Editor lacks a required managed-code test dependency: $required" }
}

$unityRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$repoRoot = Split-Path -Parent $unityRoot
$outputDirectory = Join-Path $repoRoot '.local\unity\codec-tests'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$outputAssembly = Join-Path $outputDirectory 'CodecProbe.dll'
$responseFile = Join-Path $outputDirectory 'codec.rsp'
$arguments = @('-nologo', '-target:exe', '-langversion:9.0', '-nostdlib+')
$arguments += '-out:"' + $outputAssembly + '"'
$arguments += Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' | ForEach-Object { '-reference:"' + $_.FullName + '"' }
foreach ($name in $assemblyNames) { $arguments += '-reference:"' + (Join-Path $engineDirectory $name) + '"' }
$arguments += '"' + (Join-Path $unityRoot 'Assets\Brainwash\Runtime\VoiceCapture.cs') + '"'
$arguments += '"' + (Join-Path $PSScriptRoot 'CodecProbe.cs') + '"'
$arguments | Set-Content -LiteralPath $responseFile -Encoding utf8

& $dotnet $compiler ('@' + $responseFile)
if ($LASTEXITCODE -ne 0) { throw "Codec probe compilation failed ($LASTEXITCODE)." }
foreach ($name in $assemblyNames) { Copy-Item -LiteralPath (Join-Path $engineDirectory $name) -Destination $outputDirectory -Force }
# Reuse the installed compiler's runtime target so no separate SDK is required.
Copy-Item -LiteralPath $runtimeConfig -Destination (Join-Path $outputDirectory 'CodecProbe.runtimeconfig.json') -Force
& $dotnet $outputAssembly
if ($LASTEXITCODE -ne 0) { throw "Codec checks failed ($LASTEXITCODE)." }
Write-Host "Generated files stay in the ignored local directory: $outputDirectory"
