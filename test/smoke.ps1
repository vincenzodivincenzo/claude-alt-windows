# End-to-end smoke test for claude-alt on Windows, against a fake Claude.exe
# (test\FakeClaude.cs). Runs in CI on windows-latest; safe to run locally too,
# it only touches a clone named "smoke".
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$repo   = Split-Path $PSScriptRoot -Parent
$ca     = Join-Path $repo 'claude-alt.ps1'
$tool   = Join-Path $env:LOCALAPPDATA 'claude-alt\bin\claude-alt-tool.exe'
$work   = Join-Path $env:TEMP 'claude-alt-smoke'
$lnk    = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Claude Smoke.lnk'
$desk   = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude Smoke.lnk'
$prof   = Join-Path $env:APPDATA 'Claude-smoke'
$script:failed = 0

function Check([bool]$cond, [string]$what) {
    if ($cond) { Write-Host "[ok]   $what" -ForegroundColor Green }
    else { Write-Host "[FAIL] $what" -ForegroundColor Red; $script:failed++ }
}
function Native([string]$exe, [string[]]$argv) {
    $ErrorActionPreference = 'Continue'
    @(& $exe @argv 2>&1 | ForEach-Object { "$_" })
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work | Out-Null

# 1. scripts parse
foreach ($f in $ca, $PSCommandPath) {
    $errs = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$errs)
    Check ($errs.Count -eq 0) "parses: $(Split-Path $f -Leaf)"
    $errs | ForEach-Object { Write-Host "    $_" }
}

# 2. fake Claude.exe
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fake = Join-Path $work 'fake\Claude.exe'
New-Item -ItemType Directory -Force (Split-Path $fake) | Out-Null
Native $csc @('/nologo', '/target:winexe', "/out:$fake", '/r:System.Windows.Forms.dll', (Join-Path $PSScriptRoot 'FakeClaude.cs')) | Write-Host
Check ($LASTEXITCODE -eq 0) 'fake Claude.exe compiles'
$env:CLAUDE_ALT_EXE = $fake
$env:FAKE_CLAUDE_OUT = Join-Path $work 'fake-out.txt'

# 3. new
& $ca remove smoke -RemoveData -Force 2>$null | Out-Null
& $ca new smoke 00362e -Desktop
Check ($LASTEXITCODE -eq 0) 'new smoke'
Check (Test-Path $tool) 'tool compiled'
Check (Test-Path $lnk) 'Start-menu shortcut written'
Check (Test-Path $desk) 'Desktop shortcut written'
$id = Native $tool @('read-aumid', '--lnk', $lnk) | Select-Object -Last 1
Check ($id -eq 'local.claudesmoke') "shortcut AppUserModelID (got '$id')"
Check (Test-Path (Join-Path $env:LOCALAPPDATA 'claude-alt\clones\smoke\icon.ico')) 'icon written'

# 4. recolour: a clay square must come out as #00362e
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.ColorTranslator]::FromHtml('#d97757')); $g.Dispose()
$bmp.Save((Join-Path $work 'clay.png')); $bmp.Dispose()
Native $tool @('make-icon', '--source', (Join-Path $work 'clay.png'), '--out', (Join-Path $work 'clay.ico'), '--hex', '00362e') | Write-Host
# read the 256px PNG entry straight out of the .ico
$bytes = [IO.File]::ReadAllBytes((Join-Path $work 'clay.ico'))
$count = [BitConverter]::ToUInt16($bytes, 4)
Check ($count -eq 6) "ico has 6 sizes (got $count)"
$size = [BitConverter]::ToUInt32($bytes, 6 + 8); $off = [BitConverter]::ToUInt32($bytes, 6 + 12)
$ms = New-Object IO.MemoryStream (, $bytes[$off..($off + $size - 1)])
$px = (New-Object System.Drawing.Bitmap $ms).GetPixel(128, 128)
Check ([Math]::Abs($px.R - 0x00) -le 3 -and [Math]::Abs($px.G - 0x36) -le 3 -and [Math]::Abs($px.B - 0x2e) -le 3) `
      ("recolour clay -> #00362e (got #{0:x2}{1:x2}{2:x2})" -f $px.R, $px.G, $px.B)

# 5. launch through the shortcut, like a user would
& $ca run smoke
Start-Sleep -Seconds 12
$rec = if (Test-Path $env:FAKE_CLAUDE_OUT) { Get-Content $env:FAKE_CLAUDE_OUT -Raw } else { '' }
Write-Host $rec
Check ($rec -like "*--user-data-dir=$prof*") 'Claude.exe got --user-data-dir'
Check ($rec -like "*CLAUDE_USER_DATA_DIR=$prof*") 'Claude.exe got CLAUDE_USER_DATA_DIR'
$probe = Native $tool @('probe', '--profile', $prof)
$probe | ForEach-Object { Write-Host "    $_" }
Check (@($probe | Where-Object { $_ -like 'pid*' }).Count -ge 1) 'probe finds the clone process'
Check (@($probe | Where-Object { $_ -like "window*`tlocal.claudesmoke`t*" }).Count -ge 1) 'window tagged with the clone AppUserModelID'

# 6. doctor and list
& $ca list
& $ca doctor smoke
Check ($LASTEXITCODE -eq 0) 'doctor smoke passes'

# 7. remove
Get-Process -Name Claude -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $fake } | Stop-Process -Force
Start-Sleep -Seconds 1
& $ca remove smoke -RemoveData -Force
Check ($LASTEXITCODE -eq 0) 'remove smoke'
Check (-not (Test-Path $lnk) -and -not (Test-Path $desk)) 'shortcuts removed'
Check (-not (Test-Path $prof)) 'profile removed'

if ($script:failed -gt 0) { Write-Host "$($script:failed) check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'all checks passed' -ForegroundColor Green
