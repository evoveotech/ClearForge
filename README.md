# ClearForge

Privacy-first Windows maintenance app: discover unnecessary files, quantify recoverable storage, execute reviewed cleanup safely. Flutter desktop UI + C#/.NET engine over an ACL'd named pipe.

**Status:** M0+M1 complete — engine host, contracts, read-only scanner, Flutter scan UI, 27 .NET + 6 Dart tests. No deletion is implemented yet (M2 gate).

## Layout

- `apps/desktop_flutter` — Flutter Windows UI
- `native/` — .NET 8 solution (Contracts, Engine, Windows, Host, Engine.Tests)
- `config/` — versioned cleanup rules + exclusions (reviewed, never remote-loaded)
- `fixtures/` — test fixture generators
- `docs/` — architecture, threat model, cleanup policy, product definition, release checklist
- `SPEC.md` — engineering spec; `docs/product-definition.md` — canonical product brief

## Develop

```bat
rem engine
dotnet build native\ClearForge.sln
dotnet test  native\ClearForge.sln

rem UI
cd apps\desktop_flutter
flutter analyze
flutter test
flutter run -d windows   rem requires VS "Desktop development with C++" workload
```

Prereqs: .NET SDK per `global.json` (9.0.203, targets net8.0), Flutter stable with Windows desktop support, Visual Studio 2022 + "Desktop development with C++" workload to *build* the runner (analyze/test work without it).

## Safety posture

M1 is read-only by construction: the engine exposes scan/preview/cancel/history only; `cleanup.commit` returns `cleanup_not_enabled`. Deletion lands in M2 behind session-bound candidate IDs, pre-execution revalidation, and quarantine-first semantics — see `docs/cleanup-policy.md`.

## License

Copyright (c) 2026 Evoveo Tech. All rights reserved.

ClearForge is proprietary software owned exclusively by Evoveo Tech.
See `LICENSE` and `NOTICE` for terms. No permission is granted to use,
copy, modify, or distribute this software without prior written consent.
