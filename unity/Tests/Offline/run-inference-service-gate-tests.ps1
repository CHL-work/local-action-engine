param([string]$UnityPath)
$ErrorActionPreference = 'Stop'

if (-not $UnityPath) {
    $UnityPath = @(
        $env:UNITY_PATH,
        'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $UnityPath -or -not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw 'Pass -UnityPath pointing to an installed Unity Editor. This test does not install software.'
}
$gateEditorData = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $UnityPath).Path) 'Data'
$gateDotnet = Join-Path $gateEditorData 'NetCoreRuntime\dotnet.exe'
$gateCompiler = Join-Path $gateEditorData 'DotNetSdkRoslyn\csc.dll'
$gateRuntimeConfig = Join-Path $gateEditorData 'DotNetSdkRoslyn\csc.runtimeconfig.json'
$gateReferences = Join-Path $gateEditorData 'NetStandard\ref\2.1.0'
foreach ($required in @($gateDotnet, $gateCompiler, $gateRuntimeConfig, $gateReferences)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required local compiler dependency missing: $required" }
}
$gateUnityRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$gateOutput = Join-Path (Split-Path -Parent $gateUnityRoot) '.local\unity\inference-service-gate-tests'
New-Item -ItemType Directory -Path $gateOutput -Force | Out-Null
$gateAssembly = Join-Path $gateOutput 'InferenceServiceGateProbe.dll'
$gateResponse = Join-Path $gateOutput 'inference-service-gate.rsp'
$gateArguments = @('-nologo', '-target:exe', '-langversion:9.0', '-nostdlib+', ('-out:"' + $gateAssembly + '"'))
$gateArguments += Get-ChildItem -LiteralPath $gateReferences -Filter '*.dll' | ForEach-Object { '-reference:"' + $_.FullName + '"' }
$gateArguments += '"' + (Join-Path $gateUnityRoot 'Assets\Brainwash\Runtime\InferenceServiceGate.cs') + '"'
$gateArguments += '"' + (Join-Path $PSScriptRoot 'InferenceServiceGateProbe.cs') + '"'
$gateArguments | Set-Content -LiteralPath $gateResponse -Encoding utf8
& $gateDotnet $gateCompiler ('@' + $gateResponse)
if ($LASTEXITCODE -ne 0) { throw "Inference service gate probe compilation failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath $gateRuntimeConfig -Destination (Join-Path $gateOutput 'InferenceServiceGateProbe.runtimeconfig.json') -Force
& $gateDotnet $gateAssembly
if ($LASTEXITCODE -ne 0) { throw "Inference service gate checks failed ($LASTEXITCODE)." }
