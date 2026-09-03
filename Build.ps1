<#
.SYNOPSIS
    Build.ps1 - Compile PONI.ps1 into PONI.exe (a portable, single file).

.DESCRIPTION
    Uses the PS2EXE module: it embeds the script and starts the PowerShell engine
    in-process, so there is no external dependency (Windows PowerShell 5.1 is present
    on every Windows machine).

      - Injects the logo (PONI_icon.png resized to 128px, base64) into a TEMPORARY copy
        of the script, so the .exe stays a single portable file without PONI.ps1 having
        to version a large base64 blob.
      - Gives the .exe its icon (PONI_icon.ico, via -iconFile).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Build.ps1

    Right-click > Run with PowerShell also works.
#>

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$InputPs1  = Join-Path $ScriptDir "PONI.ps1"
$OutputExe = Join-Path $ScriptDir "PONI.exe"
$IconPng   = Join-Path $ScriptDir "assets\PONI_icon.png"
$IconIco   = Join-Path $ScriptDir "assets\PONI_icon.ico"

if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Write-Host "ps2exe module not found, installing (current user)..." -ForegroundColor Yellow
    Install-Module -Name ps2exe -Scope CurrentUser -Force -AllowClobber
}
Import-Module ps2exe

# --- Prepare the source to compile: inject the base64 logo if PONI_icon.png exists ---
$srcToCompile = $InputPs1
$tempPs1 = $null
if (Test-Path $IconPng) {
    Add-Type -AssemblyName System.Drawing
    $img = [System.Drawing.Image]::FromFile($IconPng)
    try {
        $bmp = New-Object System.Drawing.Bitmap 128, 128
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.SmoothingMode     = 'HighQuality'
        $g.PixelOffsetMode   = 'HighQuality'
        $g.DrawImage($img, 0, 0, 128, 128)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $b64 = [Convert]::ToBase64String($ms.ToArray())
        $ms.Dispose()
    } finally { $img.Dispose() }

    $text = [System.IO.File]::ReadAllText($InputPs1)
    $needle = "`$LogoPngBase64 = ''"
    if (-not $text.Contains($needle)) { throw "Marker '`$LogoPngBase64 = ''''' not found in PONI.ps1." }
    $text = $text.Replace($needle, "`$LogoPngBase64 = '$b64'")
    $tempPs1 = Join-Path $env:TEMP ("PONI_build_" + [guid]::NewGuid().ToString('N') + ".ps1")
    [System.IO.File]::WriteAllText($tempPs1, $text, (New-Object System.Text.UTF8Encoding($true)))
    $srcToCompile = $tempPs1
    Write-Host ("Logo injected ({0:N0} base64 bytes)." -f $b64.Length) -ForegroundColor Yellow
} else {
    Write-Host "PONI_icon.png not found: building without an embedded logo." -ForegroundColor Yellow
}

$ps2exeArgs = @{
    inputFile   = $srcToCompile
    outputFile  = $OutputExe
    title       = "PONI"
    product     = "PONI"
    description = "Plain Open Network Interface - network-profile manager and RJ45/VM routing"
    company     = "PONI"
    version     = "1.0.0.0"
    requireAdmin = $true
    x64         = $true
    STA         = $true
    noConsole   = $true
    DPIAware    = $true
}
if (Test-Path $IconIco) { $ps2exeArgs.iconFile = $IconIco }

try {
    Invoke-ps2exe @ps2exeArgs
} finally {
    if ($tempPs1 -and (Test-Path $tempPs1)) { Remove-Item $tempPs1 -Force -ErrorAction SilentlyContinue }
}

Write-Host "`nExe generated: $OutputExe" -ForegroundColor Green
Write-Host "The exe is fully self-contained: profiles are stored in %APPDATA%\PONI\profiles.json (no file needed next to the exe)." -ForegroundColor Green
