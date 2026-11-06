param([Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$pngSignature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
$evidence = @()
foreach ($capture in @('pit_arena', 'puppet_war', 'ledge_key', 'rite_of_return', 'eye_heist',
                      'gallery_idle', 'gallery_walk', 'gallery_strike', 'gallery_dance', 'gallery_close')) {
    $screenshotPath = [System.IO.Path]::ChangeExtension($ReportPath, ".$capture.png")
    if (-not (Test-Path -LiteralPath $screenshotPath -PathType Leaf)) { throw "Missing screenshot evidence: $screenshotPath" }
    $bytes = [System.IO.File]::ReadAllBytes($screenshotPath)
    if ($bytes.Length -le 8) { throw "Empty screenshot evidence: $screenshotPath" }
    for ($index = 0; $index -lt $pngSignature.Length; $index++) {
        if ($bytes[$index] -ne $pngSignature[$index]) { throw "Invalid PNG screenshot: $screenshotPath" }
    }
    $bitmap = [System.Drawing.Bitmap]::new($screenshotPath)
    try {
        if ($bitmap.Width -lt 320 -or $bitmap.Height -lt 200) { throw "Screenshot is too small: $screenshotPath" }
        $colors = [System.Collections.Generic.HashSet[int]]::new()
        $minimum = 255
        $maximum = 0
        # A regular grid measures image content without loading another library.
        $stepX = [Math]::Max(1, [int][Math]::Floor($bitmap.Width / 64))
        $stepY = [Math]::Max(1, [int][Math]::Floor($bitmap.Height / 64))
        for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
                $color = $bitmap.GetPixel($x, $y)
                $null = $colors.Add($color.ToArgb() -band 0x00ffffff)
                $minimum = [Math]::Min($minimum, [Math]::Min($color.R, [Math]::Min($color.G, $color.B)))
                $maximum = [Math]::Max($maximum, [Math]::Max($color.R, [Math]::Max($color.G, $color.B)))
            }
        }
        if ($colors.Count -lt 8 -or ($maximum - $minimum) -lt 16) {
            throw "Blank or nearly uniform rendered screenshot: $screenshotPath (sampled colors=$($colors.Count), channel range=$($maximum - $minimum))"
        }
        $evidence += [pscustomobject]@{ capture = $capture; width = $bitmap.Width; height = $bitmap.Height; sampledColors = $colors.Count; channelRange = $maximum - $minimum }
    } finally { $bitmap.Dispose() }
}
$evidence | ConvertTo-Json | Set-Content -LiteralPath ([System.IO.Path]::ChangeExtension($ReportPath, '.render-evidence.json')) -Encoding utf8
Write-Host 'Verified ten nonblank scene screenshots.'
