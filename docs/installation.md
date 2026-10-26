# Installing ClearForge

## Status (honest)

| Channel | Status |
|---|---|
| Portable ZIP (`clearforge-windows-x64-*.zip`) | Works — unsigned, per-user |
| Inno Setup installer (`packaging/inno/clearforge.iss`) | Builds + verified: silent install/uninstall, engine spawn, data preservation — unsigned |
| Code signing / SmartScreen reputation | **BLOCKED** — needs a code-signing identity |
| macOS | **BLOCKED** — no macOS build environment has been verified |

Unsigned Windows binaries trigger SmartScreen. That is expected for an
unsigned build — do not ask users to bypass warnings silently; publish
signed builds before claiming consumer-ready install.

## Requirements

- Windows 10 (1809+) or Windows 11, x64
- No admin rights and **no .NET runtime prerequisite** — the engine is
  published self-contained (`win-x64`) inside `engine\`.

## Installer (recommended)

`packaging\inno\clearforge.iss` — build with Inno Setup 6:

```
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" packaging\inno\clearforge.iss
# (or: & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" ...)
```

Produces `dist\clearforge-setup-windows-x64-<v>.exe` — per-user install
(`%LOCALAPPDATA%\Programs\ClearForge`, no admin), Start Menu entry, and a
registered uninstall entry. Verified silent flows:

```
setup.exe /VERYSILENT /NORESTART          # install (verified: 213 files)
unins000.exe /VERYSILENT /NORESTART       # uninstall (verified: data kept)
```

Caveat when scripting from Git Bash: use PowerShell or `//VERYSILENT`
(double slash) — MSYS2 converts `/VERYSILENT` to a path and the wizard
opens interactively.

## Portable ZIP

1. `powershell packaging\build-package.ps1` → `dist\clearforge-windows-x64-<v>.zip`
2. Extract anywhere writable (e.g. `%LOCALAPPDATA%\Programs\ClearForge`).
3. Run `clearforge_desktop.exe`. The engine is spawned automatically from
   `engine\ClearForge.Host.exe`.

Verified: extracted to a clean folder, the app launches and spawns the
bundled engine on the `clearforge.engine.v1` ACL-protected pipe.

## Upgrade / uninstall

- Upgrade: close the app, replace files. Journal, quarantine, and
  preferences live under `%LOCALAPPDATA%\ClearForge` and survive upgrades.
- Uninstall: delete the folder. Recovery data (journal + quarantine)
  is intentionally left behind — delete `%LOCALAPPDATA%\ClearForge` only
  if you are certain nothing quarantined needs restoring.

## macOS

Not available. Platform implementation and verification are tracked in
`tasks/roadmap.md`; no macOS artifact exists or is claimed.
