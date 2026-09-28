<#
.SYNOPSIS
    build.ps1 - Build PONI v2 (C# / WPF, .NET Framework 4.8) into a single portable PONI.exe.

.DESCRIPTION
    Requires the .NET SDK (8.0 or later) - only to BUILD. The resulting PONI.exe needs
    nothing but Windows 10 (1903+) / 11: .NET Framework 4.8 and WPF ship with Windows.

    Output: dist\PONI.exe (+ dist\PONI.exe.sha256).

.PARAMETER Configuration
    Release (default): requires administrator rights at launch (UAC prompt), as shipped.
    Debug: runs without elevation, for UI work only.

.PARAMETER SkipTests
    Skip the unit tests (tests\PONI.Tests). By default a failing test stops the build.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\PONI\PONI.csproj'
$dist = Join-Path $root 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is required to build PONI: https://dotnet.microsoft.com/download'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\PONI.Tests\PONI.Tests.csproj') -c $Configuration -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed (exit code $LASTEXITCODE): no exe produced." }
}

dotnet build $project -c $Configuration -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

$out = Join-Path $root "src\PONI\bin\$Configuration\net48"
$files = @(Get-ChildItem $out -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'PONI.exe') {
    throw "Expected a single PONI.exe in $out, found: $($files.Name -join ', ')"
}

New-Item -ItemType Directory -Force $dist | Out-Null
$exe = Join-Path $dist 'PONI.exe'
Copy-Item $files[0].FullName $exe -Force
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
"$hash  PONI.exe" | Set-Content (Join-Path $dist 'PONI.exe.sha256') -Encoding ASCII

Write-Host ""
Write-Host "PONI.exe ($Configuration) -> $exe" -ForegroundColor Green
Write-Host ("Size: {0:N0} bytes   SHA-256: {1}" -f (Get-Item $exe).Length, $hash) -ForegroundColor Green
