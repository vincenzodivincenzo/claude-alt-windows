# claude-alt (Windows)

Run more than one Claude Desktop account on the same Windows PC. Each extra account gets its own Start-menu entry, taskbar button, recoloured icon, login and local session history.

This is the Windows sibling of [claude-alt](https://github.com/vincenzodivincenzo/claude-alt) for macOS. The official Claude install is never modified and keeps auto-updating.

## How it works

On Windows a clone doesn't need its own copy of the app. Each extra account is:

| Piece | Where | Why |
|---|---|---|
| Profile dir | `%APPDATA%\Claude-<name>` | passed as `--user-data-dir` and `CLAUDE_USER_DATA_DIR`; Chromium keys its single-instance lock on it, so the clone runs next to the main Claude |
| Start-menu shortcut | `Claude <Name>` | carries AppUserModelID `local.claude<name>` |
| Recoloured icon | `%LOCALAPPDATA%\claude-alt\clones\<name>\icon.ico` | uses the same hue remap as the macOS tool |
| Launcher | `%LOCALAPPDATA%\claude-alt\bin\claude-alt-launcher.exe` | finds the **current** `Claude.exe` on every launch, starts it on the clone's profile, then tags the clone's windows with its AppUserModelID |

The window tagging is what gives the clone its own taskbar button. Windows groups taskbar buttons by AppUserModelID, so without it every Claude window would stack under one icon. When the launcher stamps `local.claude<name>` on the clone's windows, the taskbar takes that group's name and icon from the matching Start-menu shortcut. Pinning the button then pins the clone.

The launcher is compiled on your machine by the `csc.exe` that ships with .NET Framework 4.x in every Windows 10/11 install. You don't need an SDK, and no downloaded binary has to get past SmartScreen.

## Install

```powershell
git clone https://github.com/vincenzodivincenzo/claude-alt-windows.git $env:USERPROFILE\claude-alt-windows
cd $env:USERPROFILE\claude-alt-windows
.\claude-alt.cmd new personal 00362e
```

Without git, download the ZIP and unpack it into a folder you'll keep:

```powershell
Invoke-WebRequest https://github.com/vincenzodivincenzo/claude-alt-windows/archive/refs/heads/main.zip -OutFile $env:TEMP\claude-alt.zip
Expand-Archive $env:TEMP\claude-alt.zip $env:USERPROFILE -Force
& "$env:USERPROFILE\claude-alt-windows-main\claude-alt.cmd" new personal 00362e
```

Keep the folder: the Start-menu entries don't depend on it, but `refresh`, `doctor` and `remove` run from it.

`claude-alt.cmd` runs the script with `-ExecutionPolicy Bypass`, so you don't need to change the machine's execution policy. To call it from anywhere, add the folder to your user `PATH`.

Then open **Start → Claude Personal** and sign in with **email → code**. See trap 1 for why Google sign-in doesn't work here.

## Usage

```
claude-alt list                        original + clones, running state, data location
claude-alt new <name> <hex> [-Desktop] create "Claude <Name>" (also on the Desktop with -Desktop)
claude-alt run <name>                  launch (same as the Start-menu entry)
claude-alt doctor [name]               verify - including runtime profile routing and taskbar tagging
claude-alt refresh [name]              rebuild launcher, icons, shortcuts
claude-alt remove <name> [-RemoveData] delete shortcuts; -RemoveData also deletes login + history
```

Claude updates don't require a `refresh`. Run it after you move this folder, after reinstalling Claude, or if the icons go blank.

## Traps

1. **Sign-in callbacks go to the main Claude.** `claude://` links are registered to the official app, so Google/SSO sign-in started in a clone finishes in the main window. Sign in with your email and the 6-digit code instead. The session then persists for that profile.
2. **Only one Claude can run the Cowork VM.** The VM runs as a single Windows service. If the main Claude holds it, the clone falls back to local agent mode and may show "Could not start Claude workspace / VHDX file not found". Chat and Claude Code sessions work in both. `doctor` notes this whenever both are running.
3. **MSIX moves profile data.** The Microsoft Store/MSIX build may write a clone's profile to `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude-<name>` instead of `%APPDATA%\Claude-<name>`. `list`, `doctor` and `remove -RemoveData` check both places.
4. **Split profile (the macOS lesson).** On macOS, `CLAUDE_USER_DATA_DIR` alone left IndexedDB (chats, routines) in the default profile. The launcher therefore always passes `--user-data-dir` too, and `doctor` checks at runtime that the clone's helper processes carry the profile and that browser storage is written into the clone's directory.
5. **`claude-code` memory is shared.** Both apps use `%USERPROFILE%\.claude` (settings, memory, CLAUDE.md), the same as on macOS.

## Testing

`test\smoke.ps1` runs on every push in GitHub Actions (`windows-latest`, Windows PowerShell 5.1). It compiles the launcher and a fake `Claude.exe` (`test\FakeClaude.cs`), then runs a full cycle against the fake: `new`, recolour, launch through the Start-menu shortcut, check the flag/env/window tagging, `doctor`, `remove`. It does **not** run the real Claude Desktop, so traps 1–3 come from community reports and haven't been verified here.

Set `CLAUDE_ALT_EXE` to point the launcher at any `Claude.exe`, for an unusual install or for testing.

## Prior art

- [vodongha/claude-desktop-clone](https://github.com/vodongha/claude-desktop-clone): `Get-AppxPackage` resolution and the Cowork VM / `%APPDATA%` findings
- [ajc3xc/dual-claude-launcher](https://github.com/ajc3xc/dual-claude-launcher): shortcuts breaking on MSIX version folders
