<#
claude-alt.ps1 - run extra Claude Desktop accounts on Windows, each with its
own Start-menu entry, taskbar button, recoloured icon and login.

Unlike the macOS claude-alt, nothing is copied or re-signed. A clone is:
  - a profile dir          %APPDATA%\Claude-<name>      (passed as --user-data-dir)
  - a Start-menu shortcut  "Claude <Name>"              (AppUserModelID local.claude<name>)
  - a recoloured icon      %LOCALAPPDATA%\claude-alt\clones\<name>\icon.ico
The shortcut runs claude-alt-launcher.exe (compiled locally from
launcher\ClaudeAlt.cs), which looks up the CURRENT official Claude.exe on every
launch. Claude keeps auto-updating itself and clones follow it.

Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less files as ANSI.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$Name,
    [Parameter(Position = 2)][string]$Color,
    [switch]$Desktop,      # new: also put a shortcut on the Desktop
    [switch]$RemoveData,   # remove: also delete the clone's login and local history
    [switch]$Force,        # remove -RemoveData: skip the typed confirmation
    [string]$SourceExe     # new/refresh: take the icon from this file instead of Claude.exe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$BaseHex   = 'd97757'   # the stock Claude clay orange
$StateDir  = Join-Path $env:LOCALAPPDATA 'claude-alt'
$BinDir    = Join-Path $StateDir 'bin'
$CloneRoot = Join-Path $StateDir 'clones'
$Launcher  = Join-Path $BinDir 'claude-alt-launcher.exe'
$Tool      = Join-Path $BinDir 'claude-alt-tool.exe'
$Source    = Join-Path $PSScriptRoot 'launcher\ClaudeAlt.cs'
$StartMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'

function Show-Usage {
@'
usage:
  claude-alt list                        original + all clones, running state, data location
  claude-alt new <name> <hex> [-Desktop] create "Claude <Name>" with that icon colour
  claude-alt run <name>                  launch a clone (same as its Start-menu entry)
  claude-alt doctor [name]               verify install, shortcuts and, if running, that the
                                         clone uses its own profile and its own taskbar button
  claude-alt refresh [name]              rebuild launcher, icons and shortcuts (after moving
                                         this folder, reinstalling Claude, or blank icons)
  claude-alt remove <name> [-RemoveData] delete the shortcuts; -RemoveData also deletes the
                                         clone's login and local history

examples:
  claude-alt new personal 00362e
  claude-alt doctor personal
'@ | Write-Host
}

# ------------------------------------------------------------------ helpers

function Die([string]$msg) { Write-Host "claude-alt: $msg" -ForegroundColor Red; exit 1 }

function Pretty([string]$n) { $n.Substring(0, 1).ToUpper() + $n.Substring(1) }
function ProfileOf([string]$n) { Join-Path $env:APPDATA "Claude-$n" }
function AumidOf([string]$n) { "local.claude$n" }
function CloneDir([string]$n) { Join-Path $CloneRoot $n }
function LnkOf([string]$n) { Join-Path $StartMenu "Claude $(Pretty $n).lnk" }
function DesktopLnkOf([string]$n) { Join-Path ([Environment]::GetFolderPath('Desktop')) "Claude $(Pretty $n).lnk" }

function Check-Name([string]$n) {
    if (-not $n) { Die 'missing <name>' }
    $n = $n.ToLower()
    if ($n -notmatch '^[a-z0-9][a-z0-9-]{0,30}$') { Die "bad name '$n' (letters, digits, '-'; max 31)" }
    $n
}

function Check-Hex([string]$h) {
    if (-not $h) { Die 'missing <hex> colour, e.g. 00362e' }
    $h = $h.TrimStart('#').ToLower()
    if ($h -notmatch '^[0-9a-f]{6}$') { Die "bad colour '$h' (want RRGGBB)" }
    $h
}

function Read-CloneIni([string]$n) {
    $ini = @{}
    $f = Join-Path (CloneDir $n) 'clone.ini'
    if (Test-Path $f) {
        foreach ($line in Get-Content $f) {
            if ($line -match '^\s*([^=]+?)\s*=\s*(.*)$') { $ini[$Matches[1]] = $Matches[2] }
        }
    }
    $ini
}

function Write-CloneIni([string]$n, [hashtable]$ini) {
    $lines = foreach ($k in ($ini.Keys | Sort-Object)) { "$k=$($ini[$k])" }
    Set-Content -Path (Join-Path (CloneDir $n) 'clone.ini') -Value $lines -Encoding ASCII
}

function Get-CloneNames {
    if (-not (Test-Path $CloneRoot)) { return @() }
    @(Get-ChildItem $CloneRoot -Directory | ForEach-Object { $_.Name } | Sort-Object)
}

# Runs a native exe without PS 5.1 turning its stderr into a terminating error.
# Returns its output lines; the caller reads $LASTEXITCODE.
function Invoke-Native([string]$exe, [string[]]$argv) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { @(& $exe @argv 2>&1 | ForEach-Object { "$_" }) }
    finally { $ErrorActionPreference = $prev }
}

# Arguments go in as an array: PowerShell would read a bare --source as a parameter.
function Invoke-Tool([string[]]$argv) { Invoke-Native $Tool $argv }

function Find-Csc {
    foreach ($fw in 'Framework64', 'Framework') {
        $c = Join-Path $env:WINDIR "Microsoft.NET\$fw\v4.0.30319\csc.exe"
        if (Test-Path $c) { return $c }
    }
    Die 'csc.exe not found: .NET Framework 4.x is part of Windows 10/11; turn it on in "Windows Features"'
}

# Compiles launcher\ClaudeAlt.cs into the launcher (GUI, no console flash) and
# the tool (console, so this script can read its output). Skipped when the
# source is unchanged since the last build.
function Build-Tools([switch]$Rebuild) {
    if (-not (Test-Path $Source)) { Die "missing $Source - run claude-alt from its repo folder" }
    $hash  = (Get-FileHash $Source -Algorithm SHA256).Hash
    $stamp = Join-Path $BinDir 'source.sha256'
    if (-not $Rebuild -and (Test-Path $Launcher) -and (Test-Path $Tool) -and (Test-Path $stamp) -and
        ((Get-Content $stamp -Raw).Trim() -eq $hash)) { return }

    New-Item -ItemType Directory -Force $BinDir | Out-Null
    $csc = Find-Csc
    foreach ($t in @(@{ Out = $Tool; Target = 'exe' }, @{ Out = $Launcher; Target = 'winexe' })) {
        $tmp = "$($t.Out).new"
        $out = Invoke-Native $csc @('/nologo', '/optimize+', "/target:$($t.Target)", "/out:$tmp",
                                    '/r:System.Management.dll', '/r:System.Drawing.dll', $Source)
        if ($LASTEXITCODE -ne 0) { $out | Write-Host; Die "compiling $(Split-Path $t.Out -Leaf) failed" }
        # A running exe (the watcher of an open clone) can be renamed but not
        # overwritten, so move the old one aside instead of quitting clones.
        Get-ChildItem $BinDir -Filter '*.old' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        if (Test-Path $t.Out) { Move-Item $t.Out "$($t.Out).$([guid]::NewGuid().ToString('N').Substring(0, 8)).old" }
        Move-Item $tmp $t.Out
    }
    Set-Content $stamp $hash -Encoding ASCII
    Write-Host "  built launcher + tool in $BinDir"
}

function Resolve-ClaudeExe([switch]$Refresh) {
    $a = @('resolve-exe'); if ($Refresh) { $a += '--refresh' }
    $out = Invoke-Native $Tool $a
    if ($LASTEXITCODE -ne 0) { Die ($out -join "`n") }
    ($out | Select-Object -Last 1).Trim()
}

function Get-ClaudeVersion([string]$exe) {
    try { (Get-Item $exe).VersionInfo.ProductVersion } catch { '?' }
}

# Kept identical to MatchesProfile in launcher\ClaudeAlt.cs.
function Test-ProfileArg([string]$cmdline, [string]$profileDir) {
    $pat = '--user-data-dir="?' + [regex]::Escape($profileDir.TrimEnd('\')) + '\\?(?=["\s]|$)'
    [regex]::IsMatch($cmdline, $pat, 'IgnoreCase')
}

function Get-ClaudeProcs {
    @(Get-CimInstance Win32_Process -Filter "Name = 'Claude.exe'" |
        Where-Object { $_.CommandLine } |
        Select-Object ProcessId, ParentProcessId, CommandLine, CreationDate)
}

function Is-Browser($p) { $p.CommandLine -notmatch '--type=' }

# The default profile, and where MSIX may have virtualised a clone's profile
# (a packaged process writing to %APPDATA% lands in the package's LocalCache).
function Get-DefaultProfile {
    $v = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Roaming\Claude' } | Where-Object { Test-Path $_ })
    if ($v.Count -gt 0) { return $v[0] }
    Join-Path $env:APPDATA 'Claude'
}

function Get-ProfileDirs([string]$n) {
    $dirs = @(ProfileOf $n)
    $dirs += @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "LocalCache\Roaming\Claude-$n" })
    @($dirs | Where-Object { Test-Path $_ })
}

function Has-Storage([string]$dir) {
    (Test-Path (Join-Path $dir 'IndexedDB')) -or (Test-Path (Join-Path $dir 'Local Storage'))
}

# Builds (or rebuilds) a clone's icon and shortcuts from its clone.ini.
function Build-Clone([string]$n) {
    $ini = Read-CloneIni $n
    $hex = $ini['color']
    $ico = Join-Path (CloneDir $n) 'icon.ico'
    $src = if ($SourceExe) { $SourceExe } else { Resolve-ClaudeExe }

    Write-Host "  recoloring icon #$BaseHex -> #$hex ..."
    $out = Invoke-Tool @('make-icon', '--source', $src, '--out', $ico, '--hex', $hex)
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; Die 'icon build failed' }
    $out | ForEach-Object { Write-Host "    $_" }

    New-Item -ItemType Directory -Force (ProfileOf $n) | Out-Null
    $lnks = @(LnkOf $n)
    if ($ini['desktop'] -eq '1') { $lnks += DesktopLnkOf $n }
    foreach ($l in $lnks) {
        $out = Invoke-Tool @('make-shortcut', '--lnk', $l, '--profile', (ProfileOf $n), '--aumid', (AumidOf $n),
                                  '--name', "Claude $(Pretty $n)", '--icon', $ico)
        if ($LASTEXITCODE -ne 0) { $out | Write-Host; Die "could not write $l" }
        Write-Host "  shortcut: $l"
    }
}

# ----------------------------------------------------------------- commands

function Cmd-New {
    $n = Check-Name $Name
    $hex = Check-Hex $Color
    if (Test-Path (CloneDir $n)) { Die "clone '$n' already exists (claude-alt refresh $n rebuilds it)" }
    Build-Tools
    New-Item -ItemType Directory -Force (CloneDir $n) | Out-Null
    $ini = @{ color = $hex; created = (Get-Date -Format s) }
    if ($Desktop) { $ini['desktop'] = '1' }
    Write-CloneIni $n $ini
    Write-Host "creating Claude $(Pretty $n) ..."
    Build-Clone $n
    Write-Host ""
    Write-Host "done. Start menu > Claude $(Pretty $n). Profile: $(ProfileOf $n)"
    Write-Host "sign in with the email code (Google sign-in returns to the main Claude window)."
}

function Cmd-Run {
    $n = Check-Name $Name
    $l = LnkOf $n
    if (-not (Test-Path $l)) { Die "no clone '$n' (claude-alt list)" }
    Start-Process $l
}

function Cmd-List {
    Build-Tools
    $exe = Resolve-ClaudeExe
    $procs = Get-ClaudeProcs
    $mainUp = @($procs | Where-Object { (Is-Browser $_) -and $_.CommandLine -notmatch '--user-data-dir' }).Count -gt 0
    Write-Host ("original  Claude {0}  {1}" -f (Get-ClaudeVersion $exe), $(if ($mainUp) { '[running]' } else { '' }))
    Write-Host "          exe     $exe"
    Write-Host "          profile $(Get-DefaultProfile)"
    foreach ($n in Get-CloneNames) {
        $ini = Read-CloneIni $n
        $up = @($procs | Where-Object { (Is-Browser $_) -and (Test-ProfileArg $_.CommandLine (ProfileOf $n)) }).Count -gt 0
        $flag = if (-not (Test-Path (LnkOf $n))) { '  << NO SHORTCUT (claude-alt refresh)' } else { '' }
        Write-Host ("{0,-9} Claude {1}  #{2}  {3}{4}" -f $n, (Pretty $n), $ini['color'], $(if ($up) { '[running]' } else { '' }), $flag)
        $dirs = Get-ProfileDirs $n
        if ($dirs.Count -eq 0) { $dirs = @((ProfileOf $n) + ' (not created yet)') }
        foreach ($d in $dirs) { Write-Host "          profile $d" }
    }
}

function Cmd-Refresh {
    Build-Tools -Rebuild
    $exe = Resolve-ClaudeExe -Refresh
    Write-Host "  Claude $(Get-ClaudeVersion $exe): $exe"
    $names = if ($Name) { @(Check-Name $Name) } else { Get-CloneNames }
    foreach ($n in $names) {
        if (-not (Test-Path (CloneDir $n))) { Die "no clone '$n'" }
        Write-Host "refreshing Claude $(Pretty $n) ..."
        Build-Clone $n
    }
    # make Explorer drop cached (possibly blank) shortcut icons
    $ie4 = Join-Path $env:WINDIR 'System32\ie4uinit.exe'
    if (Test-Path $ie4) { Invoke-Native $ie4 @('-show') | Out-Null }
}

function Cmd-Remove {
    $n = Check-Name $Name
    if (-not (Test-Path (CloneDir $n))) { Die "no clone '$n' (claude-alt list)" }
    $running = @(Get-ClaudeProcs | Where-Object { Test-ProfileArg $_.CommandLine (ProfileOf $n) }).Count -gt 0
    if ($RemoveData -and $running) { Die "Claude $(Pretty $n) is running - quit it first" }

    foreach ($l in @((LnkOf $n), (DesktopLnkOf $n))) {
        if (Test-Path $l) { Remove-Item $l -Force; Write-Host "  removed $l" }
    }
    Remove-Item (CloneDir $n) -Recurse -Force

    $dirs = Get-ProfileDirs $n
    if (-not $RemoveData) {
        foreach ($d in $dirs) { Write-Host "  kept account data: $d  (claude-alt new $n <hex> reuses it)" }
        return
    }
    if ($dirs.Count -eq 0) { return }
    $dirs | ForEach-Object { Write-Host "  will delete: $_" }
    if (-not $Force) {
        $a = Read-Host "type '$n' to delete this clone's login and local session history"
        if ($a -ne $n) { Write-Host '  kept account data'; return }
    }
    foreach ($d in $dirs) { Remove-Item $d -Recurse -Force; Write-Host "  deleted $d" }
}

function Cmd-Doctor {
    $script:bad = 0
    function Ok([string]$m)   { Write-Host "  [ok]   $m" -ForegroundColor Green }
    function Warn([string]$m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }
    function Bad([string]$m)  { Write-Host "  [FAIL] $m" -ForegroundColor Red; $script:bad++ }
    function Info([string]$m) { Write-Host "  [info] $m" }

    Write-Host 'install'
    Build-Tools
    Ok "launcher + tool built from $Source"
    $exe = Resolve-ClaudeExe -Refresh
    Ok "Claude $(Get-ClaudeVersion $exe): $exe"
    if ($exe -like '*\WindowsApps\*') { Info 'MSIX install: Claude updates itself; clones follow on their next launch' }

    $procs = Get-ClaudeProcs
    $mainUp = @($procs | Where-Object { (Is-Browser $_) -and $_.CommandLine -notmatch '--user-data-dir' }).Count -gt 0
    $shell = New-Object -ComObject WScript.Shell

    $names = if ($Name) { @(Check-Name $Name) } else { Get-CloneNames }
    if ($names.Count -eq 0) { Info 'no clones yet (claude-alt new <name> <hex>)' }
    foreach ($n in $names) {
        Write-Host "Claude $(Pretty $n)"
        if (-not (Test-Path (CloneDir $n))) { Bad "no clone '$n'"; continue }
        $profileDir = ProfileOf $n
        $aumid = AumidOf $n

        $l = LnkOf $n
        if (-not (Test-Path $l)) { Bad "Start-menu shortcut missing: $l (claude-alt refresh $n)" }
        else {
            $sc = $shell.CreateShortcut($l)
            if ($sc.TargetPath -ne $Launcher) { Bad "shortcut targets $($sc.TargetPath), not $Launcher (claude-alt refresh $n)" }
            elseif ($sc.Arguments -notmatch ('--profile "?' + [regex]::Escape($profileDir) + '"? --aumid "?' + [regex]::Escape($aumid) + '(?=["\s]|$)')) {
                Bad "shortcut points at another profile or ID: $($sc.Arguments)"
            }
            else { Ok "shortcut -> launcher, profile $profileDir" }
            $id = (Invoke-Tool @('read-aumid', '--lnk', $l) | Select-Object -Last 1)
            if ($id -eq $aumid) { Ok "shortcut AppUserModelID $aumid" } else { Bad "shortcut AppUserModelID is '$id', want $aumid" }
        }
        $ico = Join-Path (CloneDir $n) 'icon.ico'
        if (Test-Path $ico) { Ok "icon $ico" } else { Warn "icon missing: $ico (claude-alt refresh $n)" }

        $browsers = @($procs | Where-Object { (Is-Browser $_) -and (Test-ProfileArg $_.CommandLine $profileDir) })
        if ($browsers.Count -eq 0) {
            Info 'not running: start it and re-run doctor to check profile routing and the taskbar'
            $dirs = Get-ProfileDirs $n
            if (@($dirs | Where-Object { Has-Storage $_ }).Count -gt 0) { Ok "profile has data: $($dirs -join ', ')" }
            continue
        }
        Ok "running: pid $(($browsers | ForEach-Object { $_.ProcessId }) -join ', ') with --user-data-dir=$profileDir"

        # Chromium helpers must inherit the profile; if they do not, IndexedDB
        # (chats, routines) can land in the default profile, as it did on macOS.
        $bpids = @($browsers | ForEach-Object { $_.ProcessId })
        $kids = @($procs | Where-Object { $bpids -contains $_.ParentProcessId -and $_.CommandLine -notmatch 'crashpad' })
        $stray = @($kids | Where-Object { -not (Test-ProfileArg $_.CommandLine $profileDir) })
        if ($kids.Count -eq 0) { Info 'no helper processes yet' }
        elseif ($stray.Count -eq 0) { Ok "all $($kids.Count) helper processes carry the profile" }
        else { Warn "$($stray.Count) of $($kids.Count) helpers do not name the profile; check that chats land in $profileDir" }

        $dirs = Get-ProfileDirs $n
        $withData = @($dirs | Where-Object { Has-Storage $_ })
        $age = ((Get-Date) - ($browsers | Sort-Object CreationDate | Select-Object -First 1).CreationDate).TotalSeconds
        if ($withData.Count -gt 0) { Ok "browser storage written to $($withData -join ', ')" }
        elseif ($age -gt 60) { Bad "running for $([int]$age)s but no IndexedDB/Local Storage in $($dirs -join ', '): data may be going to the default profile" }
        else { Info 'just started: browser storage not written yet, re-run doctor in a minute' }

        $probe = Invoke-Tool @('probe', '--profile', $profileDir)
        $wins = @($probe | Where-Object { $_ -like "window`t*" })
        $untagged = @($wins | Where-Object { ($_ -split "`t")[2] -ne $aumid })
        if ($wins.Count -eq 0) { Info 'no visible windows' }
        elseif ($untagged.Count -eq 0) { Ok "$($wins.Count) window(s) tagged $aumid: own taskbar button and icon" }
        else { Warn "$($untagged.Count) window(s) not tagged yet; they group under the main Claude on the taskbar" }

        $watch = @(Get-CimInstance Win32_Process -Filter "Name = 'claude-alt-launcher.exe'" |
            Where-Object { $_.CommandLine -and $_.CommandLine -like "*$aumid*" })
        if ($watch.Count -gt 0) { Ok "watcher running (pid $($watch[0].ProcessId))" }
        else { Warn 'no watcher: windows opened from now on keep the main Claude taskbar icon (relaunch from the shortcut)' }

        if ($mainUp) { Info 'the main Claude is running too: only one of them can start the Cowork VM' }
    }
    if ($script:bad -gt 0) { Write-Host "$($script:bad) problem(s)" -ForegroundColor Red; exit 1 }
}

# --------------------------------------------------------------------- main

switch ($Command.ToLower()) {
    'new'     { Cmd-New }
    'run'     { Cmd-Run }
    'list'    { Cmd-List }
    'refresh' { Cmd-Refresh }
    'remove'  { Cmd-Remove }
    'doctor'  { Cmd-Doctor }
    default   { Show-Usage; if ($Command -notin 'help', '-h', '--help') { exit 2 } }
}
