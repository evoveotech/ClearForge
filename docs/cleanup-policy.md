# Cleanup Policy

## Principles
1. Read-only until explicit consent. Scans may never write.
2. Default-deny: a file is cleanable only when a *versioned rule* in the
   compiled catalog classifies it; unknown/unmatched files are informational.
3. Determinism: safety derives from rule membership + containment + age +
   lock state — never from filename vibes or LLM judgment.
4. Recoverability: the only mutation is quarantine-move — journaled,
   same-volume, verified, restorable. **Permanent deletion has no code path**
   (asserted by `Permanent_deletion_has_no_code_path`).
5. Honesty: `estimated_bytes` and `verified_reclaimed_bytes` are different
   numbers; report both.

## Never-clean list (cannot be enabled by config)
- Registry hives and registry entries ("cleaning" is permanently out of scope).
- Browser credential stores, cookies at password/session granularity, auth tokens.
- Windows component store (WinSxS), SoftwareDistribution live data, restore
  points, Windows.old, ESD files — any WinSxS work goes through DISM only.
- Files outside the allowlisted trusted roots.
- Locked/in-use files — skipped, never force-closed, no process termination.

## Native delegation rule
Where Windows has a supported mechanism, ClearForge wraps or hands off rather
than reimplementing: WinSxS → DISM `/StartComponentCleanup`; Recycle Bin →
`IFileOperation`/`IRecycleBin`; free-space wipe → documented overwrite pattern
comparable to `cipher /w`; startup entries → supported registry/Startup-folder
mechanisms with Settings handoff where direct control is unavailable.
