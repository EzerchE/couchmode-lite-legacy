# Builds AutoXboxMode.exe (.NET Framework 4.8, no external dependencies).
# Output: build\AutoXboxMode.exe

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$src = Join-Path $root "src\AutoXboxMode.cs"
$outDir = Join-Path $root "build"
$out = Join-Path $outDir "AutoXboxMode.exe"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4.x) not found." }

$refs = @(
    "System.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll"
) | ForEach-Object { "/r:$_" }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$out" $refs $src

if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit $LASTEXITCODE)." }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "Built $out ($size KB)" -ForegroundColor Green
