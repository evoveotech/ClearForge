# M8.5 — Startup completeness (read-only)

## Scope (implemented)

- `RunKeyStartupSource` gains a `source` label and optional `approvalOf`
  (value name → StartupApproved blob). Backward compatible — both optional.
- `StartupApprovedState.Decode`: REG_BINARY byte 0 → `0x02`/`0x03` enabled,
  `0x06` disabled, anything else `unknown`; absent record → enabled. Never
  guesses undocumented states.
- `WindowsStartupInventory.All()` (renamed from `CurrentUser()` — it now
  covers machine hives): HKCU Run + RunOnce, HKLM Run + RunOnce (64-bit),
  HKLM Run (32-bit), each joined to its StartupApproved lookup; startup
  folder unchanged. StartupApproved is not redirected — the HKLM lookup is
  shared across Run views (documented approximation).
- Per-source failure isolation via the existing composite.

## Deferred (M8.5b)

- Task Scheduler entries and services (read-only first).
- `StartupApproved\StartupFolder` join for folder entries.
- Toggling (write `StartupApproved`) — journaled reversible action, separate
  gate.

## Verification

- `dotnet test` filtered (M85+M3): 12/12 — decode matrix incl. unknown-state
  honesty, disabled join, per-source labels, legacy no-lookup behavior.
- Full suite: see commit message count.

## Honesty notes

- StartupApproved blob semantics are third-party documented, not official —
  unknown states surface as `unknown`, not `enabled`.
- HKLM 64/32 Run views share one StartupApproved hive; an entry disabled in
  one view reports disabled in both (documented approximation).
