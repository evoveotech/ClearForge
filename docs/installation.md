# Installing ClearForge

## Status (honest)

| Channel | Status |
|---|---|
| Portable ZIP (`clearforge-windows-x64-*.zip`) | Works — unsigned, per-user |
| Inno Setup installer (`packaging/inno/clearforge.iss`) | Script ready — requires Inno Setup 6 + signing for release |
| Code signing / SmartScreen reputation | **BLOCKED** — needs a code-signing identity |
| macOS | **BLOCKED** — no macOS build environment has been verified |

Unsigned Windows binaries trigger SmartScreen. That is expected for an
unsigned build — do not ask users to bypass warnings silently; publish
signed builds before claiming consumer-ready install.

## Requirements

- Windows 10 (1809+) or Windows 11, x64
- .NET 8 Desktop Runtime is **bundled requirement** — the engine
  (`engine\ClearForge.Host.exe`) is framework-dependent. Install the
  [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)
  or ship a self-contained engine build (see below).
- No admin rights required — everything runs per-user.

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

## Self-contained engine (no runtime dependency)

`dotnet publish native/ClearForge.Host -c Release -r win-x64 --self-contained`
— larger package (~70 MB) but removes the .NET runtime prerequisite.
Consider this for the first public package; the packaging script currently
produces framework-dependent output.

## macOS

Not available. Platform implementation and verification are tracked in
`tasks/roadmap.md`; no macOS artifact exists or is claimed.
