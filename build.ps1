# Builds HPD20Companion.exe with the C# compiler that ships with Windows - no SDK, no NuGet.
#
#   .\build.ps1                build dist\HPD20Companion.exe (+ the portable CLI)
#   .\build.ps1 -Test          build, then run every self-test suite
#   .\build.ps1 -SkipCli       build only the GUI
#
# Requires .NET Framework 4.x (present on Windows 10/11 by default).
# The CLI in cli\ is the cross-platform build: it uses the .NET SDK (dotnet build cli) on any OS,
# but the same sources are compiled here with csc as a portability check.

param(
    [switch]$Test,
    [string]$BackupPath,
    [switch]$SkipCli
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
& $csc /nologo /target:winexe /define:WINDOWS /out:$exe `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }
Write-Host ("built: {0} bytes" -f (Get-Item $exe).Length)

# ---- portable CLI: the same sources that cli\Hpd20Cli.csproj builds on any OS
$cliDir = Join-Path $outDir 'cli'
$cliExe = Join-Path $cliDir 'hpd20cli.exe'
if (-not $SkipCli) {
    New-Item -ItemType Directory -Force -Path $cliDir | Out-Null
    $cliSources = @(
        (Join-Path $root 'src\BackupFile.cs'),
        (Join-Path $root 'src\WavFile.cs'),
        (Join-Path $root 'src\AudioFormats.cs'),
        (Join-Path $root 'src\InstrumentTable.cs'),
        (Join-Path $root 'src\WaveEditor.cs'),
        (Join-Path $root 'src\MfAudio.cs'),
        (Join-Path $root 'tests\BackupDiff.cs'),
        (Join-Path $root 'cli\Program.cs')
    )
    Write-Host "compiling the portable CLI (Windows defines only) -> $cliExe"
    & $csc /nologo /target:exe /define:WINDOWS /out:$cliExe `
        /reference:System.dll /reference:System.Core.dll $cliSources
    if ($LASTEXITCODE -ne 0) { throw "CLI compile failed ($LASTEXITCODE)" }
    Write-Host ("built: {0} bytes" -f (Get-Item $cliExe).Length)
}

if ($Test) {
    $results = Join-Path $root 'test-results'
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $suites = @('padtest', 'hwtest', 'selftest', 'uitest', 'audiotest', 'formattest', 'wavetest', 'usbtest', 'backuptest')
    $fail = 0
    foreach ($s in $suites) {
        & $exe "--$s" (Join-Path $results "$s.txt") | Out-Null
        if ($LASTEXITCODE -ne 0) { $fail++; Write-Warning "$s failed" }
    }
    if ($BackupPath) {
        & $exe --backupcheck $BackupPath (Join-Path $results 'backupcheck.txt') | Out-Null
        if ($LASTEXITCODE -ne 0) { $fail++; Write-Warning 'backupcheck failed' }
    }

    # ---- CLI smoke test: read a real file, convert it, inspect a real backup
    if (-not $SkipCli) {
        $report = Join-Path $results 'clitest.txt'
        $lines = New-Object System.Collections.Generic.List[string]
        $cliFail = 0
        function Check([string]$name, [bool]$ok, [string]$detail) {
            if (-not $ok) { $script:cliFail++ }
            $tag = 'PASS  '
            if (-not $ok) { $tag = 'FAIL  ' }
            $suffix = ''
            if ($detail) { $suffix = '  ' + $detail }
            $lines.Add($tag + $name + $suffix)
        }

        $source = Join-Path $env:TEMP 'hpd20-clitest'
        New-Item -ItemType Directory -Force -Path $source | Out-Null
        $probe = Join-Path $source 'probe.aiff'
        if (-not (Test-Path $probe)) {
            & $exe --formattest (Join-Path $results 'formattest-tmp.txt') | Out-Null
            Copy-Item (Join-Path $env:TEMP 'hpd20-formattest\tone.aiff') $probe -ErrorAction SilentlyContinue
        }

        if (Test-Path $probe) {
            $info = & $cliExe info $probe 2>&1 | Out-String
            Check 'info reads an AIFF' ($info -match 'AIFF' -and $info -match '44100 Hz') ''
            $convOut = Join-Path $source 'out'
            $conv = & $cliExe convert $probe --out $convOut --mono 2>&1 | Out-String
            $made = Join-Path $convOut 'probe.wav'
            Check 'convert writes a WAV' ((Test-Path $made) -and ($conv -match '^ok')) ''
            if (Test-Path $made) {
                $back = & $cliExe info $made 2>&1 | Out-String
                Check 'converted file is 44.1 kHz mono 16-bit' ($back -match '44100 Hz' -and $back -match 'channels  1' -and $back -match '16-bit') ''
            }
            $fmts = & $cliExe formats 2>&1 | Out-String
            Check 'formats lists the containers' ($fmts -match '\.aiff' -and $fmts -match '\.flac') ''
        } else {
            $lines.Add('SKIP  CLI smoke test - no probe file available')
        }

        $factory = 'E:\Drivers\Roland\HandSonic HPD20\hpd20_factory\hpd20_factory\Roland\HPD-20\Backup\BKUP-001.HS0'
        if (Test-Path $factory) {
            $binfo = & $cliExe backup-info $factory 2>&1 | Out-String
            Check 'backup-info validates a real image' ($binfo -match 'md5       ok' -and $binfo -match 'kits      200 named') ''
        } else {
            $lines.Add('SKIP  backup-info - factory backup not present')
        }

        $lines.Add('')
        $lines.Add($(if ($cliFail -eq 0) { 'CLI CHECKS PASSED' } else { "$cliFail CLI CHECK(S) FAILED" }))
        Set-Content -Path $report -Value ($lines -join [Environment]::NewLine) -Encoding UTF8
        if ($cliFail -gt 0) { $fail++; Write-Warning 'CLI checks failed' }
    }

    Write-Host "self-tests: $(($suites.Count + $(if ($BackupPath) { 1 } else { 0 })) - $fail) suite(s) passed, $fail failed"
    if ($fail -gt 0) { exit 1 }
}
