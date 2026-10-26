# ClearForge — Product Definition (canonical)

Source: owner's product brief. This document is the authority; `../SPEC.md` is the derived engineering specification.

## 1. Product definition

**ClearForge** (working name — check trademark and domain availability before commercial use).

A privacy-first Windows maintenance application that discovers unnecessary files, quantifies recoverable storage and executes reviewed cleanup operations safely.

- **Storage** — junk, caches, large files, duplicates
- **Performance** — startup impact, resource usage
- **Privacy** — browser data review and privacy audit
- **Control** — review, exclusions, audit and recovery

### Target platform
- Windows 11 x64 first; Windows 11 ARM64 after validating native dependencies.
- Windows 10 only with a deliberate, tested servicing/security-support policy.
- Local-first; no account required for basic cleaning.
- Free tier: transparent scanning + basic cleanup. Paid tier: advanced analysis, automation, maintenance reporting.

### Non-goals for first release
- Registry cleaners or automatic registry repair.
- Automatic driver updates.
- RAM "optimisation" or claims that cache deletion boosts performance.
- Forced termination of applications or disabling antivirus.
- Automated deletion of restore points, Windows component-store files or protected system data.

## 2. REA research workflow

Treat REA as a research assistant and observation workflow, not a source-code generator for someone else's proprietary implementation.

### Research targets
- **CCleaner Free/Professional** — visible scan categories, selected defaults, cleanup previews, exclusions, settings, subscription gates, reporting.
- **Windows Storage Sense** — native behaviour; which tasks to delegate to Windows rather than reimplement.
- **Task Manager and Settings** — existing startup controls, installed-app management, storage categories; avoid misleading duplication.

### REA execution plan
1. Capture baseline — product version, Windows version, scan inputs, visible results, selected defaults; disposable test profiles.
2. Map workflows — each user action, resulting state, warning, cancellation path, error message.
3. Build behaviour matrix — native Windows capability / independently implementable filesystem op / app-specific integration / out of scope.
4. Create test fixtures — temp files, expired logs, duplicates, locked files, symlinks, protected files; VM only.
5. Write acceptance criteria — observable inputs/outputs; no proprietary source, internal algorithms, branding or protected assets.
6. Implement independently — documented Windows integrations + own deterministic rules engine; record evidence and assumptions.

### Per-session research prompt
> You are the REA research analyst for ClearForge, an independently developed Windows maintenance application.
> Research the observable behaviour of the specified cleaner feature. Do not extract, reproduce or circumvent proprietary implementation details, licensing, DRM or access controls.
> Return: feature purpose and user-visible workflow; inputs/outputs/settings/defaults; filesystem locations or Windows integrations only where publicly documented or legitimately observable; data-loss risks and protected exclusions; error handling, cancellation and recovery; native Windows equivalents; independent implementation proposal in C#/.NET; positive/negative/boundary/regression tests; evidence links, confidence level, unresolved questions.
> Separate observed facts from assumptions. Never infer deletion safety solely from filename, age or extension. Never execute destructive actions during research.
> Output a structured Markdown specification suitable for implementation and code review.

## 3. Repository structure

Monorepo: Flutter UI, C# engine, shared protocol, test fixtures, installer evolve together.

```
clearforge/
├── apps/
│   └── desktop_flutter/
│       ├── lib/
│       │   ├── app/
│       │   ├── core/{ipc,models,state}/
│       │   ├── features/{dashboard,cleaner,storage,duplicates,startup,privacy,history,settings}/
│       │   └── main.dart
│       └── test/
├── native/
│   ├── ClearForge.Engine/{Scanning,Classification,Policies,Cleanup,Duplicates,Startup,Privacy,Storage,Diagnostics}/
│   ├── ClearForge.Contracts/
│   ├── ClearForge.Windows/
│   ├── ClearForge.Host/
│   └── ClearForge.Engine.Tests/
├── config/{cleanup-rules,exclusions,product-features}/
├── fixtures/{temp-files,duplicates,locked-files,reparse-points}/
├── integration_tests/
├── installer/
├── docs/{architecture.md,threat-model.md,rea-research/,cleanup-policy.md,release-checklist.md}
├── scripts/
└── README.md
```

Dependency rule: `ClearForge.Engine` holds core logic, depends on contracts/abstractions only — never Flutter, UI state, or network services. `ClearForge.Windows` = Windows-specific integrations. `ClearForge.Host` = local IPC endpoint. Cleanup rules are versioned, reviewed, test-covered; **no downloadable executable rules or remote code at runtime**.

## 4. Architecture and process boundaries

```
Flutter UI (Dart) ── named pipes, JSON, request IDs, cancellation, schema validation ──► C# engine host (normal user privileges)
                                                                              │  scan service · rule engine · cleanup executor · audit+SQLite
                                                                              └─► privileged ops: separate narrowly-scoped authenticated elevation mechanism (if ever required)
```

### IPC decision
Windows named pipes between Flutter and the .NET host. Secure explicitly: verify connecting user's Windows identity, restrictive ACLs, validate all message fields, prevent untrusted clients submitting arbitrary paths.

No unrestricted `deletePath(path)` endpoint. Allowlisted workflow:

`StartScan` · `GetScanResults` · `PreviewCleanup` · `CommitCleanup` · `CancelOperation` · `GetOperationStatus` · `GetCleanupHistory`

`CommitCleanup` references a server-generated session ID + candidate IDs. Engine revalidates each candidate against its scan record, current filesystem state and cleanup policy before acting.

### Core contracts

```csharp
public enum RiskLevel { Low, Medium, High, Restricted }

public sealed record ScanCandidate(
    string Id, string RuleId, string Category, string DisplayName,
    long EstimatedBytes, RiskLevel Risk, string Reason,
    bool RequiresExplicitConfirmation);

public sealed record CleanupPreview(
    string SessionId, int CandidateCount, long EstimatedBytes,
    IReadOnlyList<ScanCandidate> Candidates);

public sealed record CleanupItemResult(
    string CandidateId, string Status, long BytesReclaimed, string? ErrorCode);
```

Requirements: stable opaque candidate IDs; canonical paths stay inside the engine; no sensitive full paths in telemetry; `estimated` ≠ `verified_reclaimed` bytes; cooperative cancellation with honest partial-completion reporting; structured error codes, never raw exceptions/stack traces to the UI.

## 5. Safe cleanup engine

### Lifecycle
`Start scan → enumerate approved roots → apply exclusions → classify candidates → store session+findings → display review/risk → user selects → preview → confirm → revalidate identity+path+file state → policy check → quarantine or supported cleanup → verify → audit (cancel path exits without deletion)`

### Classification rules — deterministic rule engine, not an LLM

```yaml
id: user-temp-expired-files
version: 1
category: temporary-files
scope: current-user
root: known-user-temp-directory
minimum_age_hours: 24
risk: low
requires_confirmation: true
action: quarantine
max_files_per_scan: 50000
exclude_locked: true
follow_reparse_points: false
```

Every field validated/constrained; `root` resolves through a trusted root registry, never arbitrary user paths. Age alone never makes a file disposable — the rule must also establish supported category membership and that the file is not protected, in use, or outside its permitted root.

### Filesystem safety requirements
- Canonical paths; verify containment within approved roots.
- Never follow symlinks/junctions/reparse points during recursive scanning.
- TOCTOU: reopen + revalidate target immediately before each operation.
- Skip locked, inaccessible or changing files.
- Bounded parallelism, cancellation tokens, scan limits.
- Never follow user-controlled paths into Windows, app databases, protected dirs.
- Hard links, ADS, sparse and cloud-backed files handled carefully — logical size ≠ recoverable space.
- Audit entries before and after destructive operations.

### Quarantine vs permanent deletion
Quarantine preferred for eligible user-level files, but not universally safe: needs disk space, secure storage, no overwrite of existing files; cross-volume moves aren't atomic; active databases and special-semantics files must not be moved blindly.

For supported disposable files: revalidate → safe move into app-owned quarantine → verify move + metadata → record → restorable while retained. For native Windows maintenance: invoke a supported Windows mechanism or direct the user there — never manually delete protected update-store files.

## 6. Feature implementation specifications

P0 = MVP blocker · P1 = post-MVP · P2 = later

### A. Junk and cache cleaner — P0
Modules: `Scanning`, `Classification`, `Cleanup`
- Targets: current-user temp dir; app-owned temp folders via trusted config; stale disposable logs/crash dumps where ownership+retention known; browser cache only after browser rules validated; optional Recycle Bin via supported Windows mechanism.
- Acceptance: read-only cancellable scan; results show category/size/age/risk/reason; locked files skipped; every cleanup op auditable with actual outcomes.

### B. Storage analyser and duplicates — P0/P1
Modules: `Storage`, `Duplicates`
- Enumerate user-selected roots with explicit scope/exclusions; group by size → hash → full-content compare before declaring identical; never auto-deduplicate; report count, logical bytes, probable recoverable bytes, protected exclusions separately.
- Acceptance: identical content ≠ similar names; original never auto-selected; hard links and cloud files accounted for.

### C. Startup manager — P1
Module: `Startup`
- Read supported per-user/machine startup entries, startup folders, scheduled-task metadata; show publisher, path, trigger, available impact evidence; open Windows Settings when no supported direct control exists.
- Acceptance: read-only inventory without elevation where permitted; unknown publishers labelled unknown, not malicious; nothing disabled/removed without explicit confirmation.

### D. Privacy cleaner — P1
Module: `Privacy`
- Detect supported browser profiles; separate controls for cache/history/cookies/site data; warn about sign-outs and state loss; never touch passwords, tokens, credentials, auth stores.
- Acceptance: independent preview+consent per category; no modifying live browser databases; unsupported browser versions produce clear skip.

### E. Scheduled maintenance and reports — P2
Modules: `Diagnostics`, `Storage`
- Task Scheduler only after explicit opt-in; unattended cleanup needs separately configured approval rules; read-only scheduled scans first; local reports of findings/outcomes/failures.

### Space recovery calculation
Never sum file sizes and call it "space recovered". Track separately:
- `estimated_bytes` — sum of candidate sizes
- `selected_bytes` — sum of selected candidate sizes
- `processed_bytes` — bytes processed by the operation
- `verified_reclaimed_bytes` — verified change attributable to the operation, where measurable

Compression, sparse allocation, hard links, dedup and storage virtualization make physical recovery differ from logical size; report an estimate when exact attribution can't be established.

## 7. Security and privacy requirements

| Area | Requirement |
|---|---|
| Privileges | Unprivileged by default; no permanently elevated UI or service |
| IPC | Restrictive named-pipe ACLs, peer validation, strict schemas |
| Filesystem | Trusted roots, path containment, revalidation before deletion |
| Configuration | Signed/integrity-checked releases; versioned local rules |
| Privacy | No file contents uploaded; telemetry off by default |
| Auth data | Credentials, tokens, password stores, secrets excluded |
| Updates | Signed manifests and package signatures |
| Audit | Local bounded logs with sensitive path redaction |
| Failure recovery | Safe partial completion; quarantine recovery |
| Supply chain | Dependency scanning, pinned versions, reproducible releases where practical |

Commercial distribution: sign installer + native binaries, publish privacy policy, define vulnerability-reporting channel, test upgrade/uninstall/rollback. Never claim "100% safe", "fixes all Windows errors", "guaranteed performance boost".

## 8. Testing strategy

Destructive-operation tests must pass before any real cleanup is enabled.

| Layer | Scenarios | Pass condition |
|---|---|---|
| Unit | rules, exclusions, age thresholds, risk classification | deterministic decisions |
| Filesystem | locked files, permission denied, reparse points, path traversal | no escape from approved roots |
| Duplicate detection | identical files, same-name/different-content, large files | no false identity claims |
| IPC security | unauthorised client, malformed JSON, oversized requests | rejected safely |
| Cleanup integration | cancellation, crash, partial failure, quarantine restore | no unreported partial deletion |
| Windows integration | startup inventory, browser profiles, native cleanup handoff | correct permissions/behaviour |
| Performance | large trees, slow disks, cancellation under load | bounded memory, responsive UI |
| Release | upgrade, uninstall, signature verification, rollback | no orphaned privileged components |

Mandatory destructive scenarios: file changes between scan and cleanup; junction outside permitted root; candidate locked by active app; disk full during quarantine; crash after move before record; cancel mid-scan; running browser's locked profile DB; malicious local client requesting arbitrary-path deletion. **VM snapshots + synthetic files only — never real system dirs.**

## 9. Milestones

| Gate | Scope | Exit criteria |
|---|---|---|
| M0 Foundation | Flutter project + .NET solution; contracts + error codes; local host w/ health check + cancellation; CI, formatting, static analysis, unit tests | Flutter starts engine, submits request, gets validated response |
| M1 Scanner | Trusted roots + exclusions; bounded traversal + metadata; results table + category summaries; fixtures + containment tests | No file modified during scan; every result classified + explained |
| M2 Cleanup | Session-bound candidate IDs; preview/confirm/quarantine/restore; pre-execution revalidation; audit records + partial-failure status | All destructive tests pass in VM |
| M3 Intelligence | Hash-based duplicates; read-only startup inventory; privacy controls + native handoffs; browser behaviour validated | All modules share policy engine + consent model |
| M4 Release | MSIX or signed installer; licensing for paid features; upgrade/uninstall/rollback tests; privacy/support/disclosure policies | Signed RC passes full regression |

## 10. Initial developer commands

```bat
mkdir clearforge && cd clearforge
flutter create --platforms=windows apps/desktop_flutter
dotnet new sln -n ClearForge -o native
dotnet new classlib -n ClearForge.Contracts -o native/ClearForge.Contracts
dotnet new classlib -n ClearForge.Engine -o native/ClearForge.Engine
dotnet new classlib -n ClearForge.Windows -o native/ClearForge.Windows
dotnet new console -n ClearForge.Host -o native/ClearForge.Host
dotnet new xunit -n ClearForge.Engine.Tests -o native/ClearForge.Engine.Tests
dotnet sln native/ClearForge.sln add native/ClearForge.Contracts native/ClearForge.Engine native/ClearForge.Windows native/ClearForge.Host native/ClearForge.Engine.Tests
rem + project references per dependency rule
flutter doctor && flutter build windows --debug
dotnet build native/ClearForge.sln && dotnet test native/ClearForge.sln
```

Pin a supported .NET LTS via `global.json`; confirm generated solution filename/SDK targets before CI.

## 11. First coding task (M0 + M1)

Scope: buildable solution; versioned request/response contracts; authenticated named-pipe IPC host w/ restrictive ACLs + strict validation; read-only scanner over an explicit allowlist of current-user temp dirs; canonical containment, no reparse traversal, bounded concurrency, cancellation, scan limits; deterministic classification + exclusions + structured errors; Flutter screen with progress/findings/sizes/risk/explanations/cancel; unit + IPC integration + adversarial filesystem fixture tests; docs + CI commands.

Non-negotiables: no deletion in M1; no admin; no whole-disk default scan; no browser credentials/private DBs/protected dirs; no arbitrary-path commands from client; no unverified test claims.

## 12. Final architecture decisions

UI: Flutter Windows · Engine: C#/.NET · IPC: named pipes w/ ACLs + client validation · First targets: allowlisted user temp files · Classification: deterministic versioned rules · Deletion safety: session-bound selection, revalidation, quarantine · Startup: read-only first · Browser privacy: per-category consent, no credentials · Automation: opt-in read-only scans first · Distribution: signed MSIX or installer after deployment evaluation.

**Differentiator: trustworthy, explainable maintenance — not the file-count headline.**
