# Builds HPD20Companion.exe with the C# compiler that ships with Windows - no SDK, no NuGet.
#
#   .\build.ps1                build dist\HPD20Companion.exe
#   .\build.ps1 -Test          build, then run every self-test suite
#
# Requires .NET Framework 4.x (present on Windows 10/11 by default).

param(
    [switch]$Test,
    [string]$BackupPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "C# compiler not found: install .NET Framework 4.x"
}

$outDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir 'HPD20Companion.exe'

$sources = @()
$sources += Get-ChildItem (Join-Path $root 'src')   -Filter *.cs | ForEach-Object { $_.FullName }
$sources += Get-ChildItem (Join-Path $root 'tests') -Filter *.cs | ForEach-Object { $_.FullName }

Write-Host ("compiling {0} source files -> {1}" -f $sources.Count, $exe)
& $csc /nologo /target:winexe /out:$exe `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }
Write-Host ("built: {0} bytes" -f (Get-Item $exe).Length)

if ($Test) {
    $results = Join-Path $root 'test-results'
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $suites = @('padtest', 'selftest', 'uitest', 'audiotest', 'usbtest', 'backuptest')
    $fail = 0
    foreach ($s in $suites) {
        & $exe "--$s" (Join-Path $results "$s.txt") | Out-Null
        if ($LASTEXITCODE -ne 0) { $fail++; Write-Warning "$s failed" }
    }
    if ($BackupPath) {
        & $exe --backupcheck $BackupPath (Join-Path $results 'backupcheck.txt') | Out-Null
        if ($LASTEXITCODE -ne 0) { $fail++; Write-Warning 'backupcheck failed' }
    }
    Write-Host "self-tests: $(($suites.Count + $(if ($BackupPath) { 1 } else { 0 })) - $fail) suite(s) passed, $fail failed"
    if ($fail -gt 0) { exit 1 }
}

