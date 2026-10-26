# M8.4 — Application inventory (M8.4a: read-only registry inventory)

## Baseline (verified)

- HEAD `3bac212` (M8.3 committed). `dotnet test`: 337/337. `flutter test`:
  56/56; `flutter analyze` clean.
- Inventory conventions reused: `IStartupSource` seam + `CompositeStartupInventory`
  per-source failure isolation; dispatcher optional-module → `policy_refused`;
  screens render raw entry maps (startup pattern).

## Scope (M8.4a — implemented this milestone)

- `apps.list` — read-only inventory of the three classic Uninstall hives:
  HKLM 64-bit, HKLM 32-bit (WOW6432Node), HKCU. Per-source failure isolation;
  per-subkey read errors skipped.
- `InstalledApp` fields: name, publisher, version, installLocation, sizeBytes
  (`EstimatedSizeInKB`×1024, null when unreported/negative), installDate
  (yyyyMMdd → ISO when parseable, raw passthrough otherwise), source, scope,
  kind (`app`/`system-component`/`update`), hasUninstaller, confidence
  (`registered`/`listed-only`).
- Blank DisplayName rows skipped (not user-visible evidence). No cross-hive
  dedup — the same product can legitimately register machine + per-user;
  source is reported instead of guessed.
- Flutter: Apps nav destination; load + name/publisher filter + system/update
  chip; honest read-only note. No mutation affordances.

## M8.4b (this session) — per-app leftover map, review-only

- `apps.leftovers {name, publisher?, installLocation?}` → exact
  normalized-name matches under Roaming/Local/ProgramData. `exact` vs
  `publisher-context` confidence, `reviewOnly: true`, never-flag token
  guard, per-root failure isolation, nested Vendor\App dirs one level deep.
- Substring matching deliberately absent; orphan-detection deferred
  (unattributed dirs need a separate evidence model — FP risk).
- Remaining M8.4b work: Apps-screen leftovers surface, guided uninstall
  deep-link, Appx enumeration decision, session-bound leftover quarantine
  (needs its own milestone + approval flow).

## Deliberately excluded (explicit, not forgotten)

- Appx/MSIX enumeration (PackageManager/PowerShell) — M8.4b evaluation.
- Uninstall execution / `ms-settings` deep-link — M8.4b, only ever guided.
- Leftover/lineage mapping (port `OrphanedAppFiles` semantics) — M8.4b,
  review-only first; name-matching FP guardrails required before any binding.
- Unused-app detection — no reliable Windows usage signal; M8.4b+ honesty
  markers required (`usageDataUnavailable` analogue).

## Files

- new: `Engine/Apps/AppInventory.cs`, `Windows/WindowsAppInventory.cs`,
  `Engine.Tests/M84Tests.cs`, `lib/features/apps/apps_screen.dart`,
  `test/apps_inventory_test.dart`, `tasks/m84-plan.md`
- edit: `Contracts/Protocol.cs` (`apps.list`), `Contracts/Models.cs`
  (`AppEntryDto`, `AppListResponse`), `Host/RequestDispatcher.cs`
  (`_apps` + `AppsList()`), `Host/Program.cs` (wiring),
  `lib/core/state/engine_controller.dart` (`loadApps`),
  `lib/app/clearforge_app.dart` (nav), `docs/architecture.md` (+ stale
  `cleanup_not_enabled` trust-boundary claim corrected)

## Verification

- `dotnet build` 0 errors. `dotnet test`: **355/355** (+18 M8.4 tests:
  field mapping, kind classification, blank-name skip, size guards, date
  normalization, source isolation, dispatch contract incl. fail-closed
  `policy_refused`).
- `flutter analyze` clean; `flutter test`: **61/61** (+5 shape/filter tests).

## Risks / honesty

- Read-only; no data-loss surface added. Registry paths disclosed over the
  owner-only pipe — consistent with `duplicates.group` member paths.
- Registry `EstimatedSizeInKB` is self-reported and often missing/wrong;
  surfaced as `sizeBytes` without inflating claims. Appx absence is disclosed
  in the response `note`, not hidden.
