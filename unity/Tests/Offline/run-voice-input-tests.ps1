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
$voiceEditorData = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $UnityPath).Path) 'Data'
$voiceDotnet = Join-Path $voiceEditorData 'NetCoreRuntime\dotnet.exe'
$voiceCompiler = Join-Path $voiceEditorData 'DotNetSdkRoslyn\csc.dll'
$voiceRuntimeConfig = Join-Path $voiceEditorData 'DotNetSdkRoslyn\csc.runtimeconfig.json'
$voiceReferences = Join-Path $voiceEditorData 'NetStandard\ref\2.1.0'
foreach ($required in @($voiceDotnet, $voiceCompiler, $voiceRuntimeConfig, $voiceReferences)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required local compiler dependency missing: $required" }
}
$voiceUnityRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$voiceOutput = Join-Path (Split-Path -Parent $voiceUnityRoot) '.local\unity\voice-input-tests'
New-Item -ItemType Directory -Path $voiceOutput -Force | Out-Null
$voiceAssembly = Join-Path $voiceOutput 'VoiceInputProbe.dll'
$voiceResponse = Join-Path $voiceOutput 'voice-input.rsp'
$voiceArguments = @('-nologo', '-target:exe', '-langversion:9.0', '-nostdlib+', ('-out:"' + $voiceAssembly + '"'))
$voiceArguments += Get-ChildItem -LiteralPath $voiceReferences -Filter '*.dll' | ForEach-Object { '-reference:"' + $_.FullName + '"' }
$voiceArguments += '"' + (Join-Path $voiceUnityRoot 'Assets\Brainwash\Runtime\VoiceInputState.cs') + '"'
$voiceArguments += '"' + (Join-Path $PSScriptRoot 'VoiceInputProbe.cs') + '"'
$voiceArguments | Set-Content -LiteralPath $voiceResponse -Encoding utf8
& $voiceDotnet $voiceCompiler ('@' + $voiceResponse)
if ($LASTEXITCODE -ne 0) { throw "Voice input probe compilation failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath $voiceRuntimeConfig -Destination (Join-Path $voiceOutput 'VoiceInputProbe.runtimeconfig.json') -Force
& $voiceDotnet $voiceAssembly
if ($LASTEXITCODE -ne 0) { throw "Voice input checks failed ($LASTEXITCODE)." }
