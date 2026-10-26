## Summary

<!-- What does this change do and why? Link the issue if one exists. -->

## Legal

- [ ] I have read and agree to the ClearForge CLA (CLA.md). My contribution
      is assigned/licensed to Evoveo Tech as described there.
- [ ] This contribution is my original work and I have the right to submit it.

## Safety checklist (this is a file-mutating tool — answer honestly)

- [ ] No new filesystem mutation path outside `QuarantineExecutor`
- [ ] Preview → approval → commit pipeline unchanged (or explicitly justified)
- [ ] Session expiry / cancellation still fail closed
- [ ] No paths, credentials, or user data in logs, diagnostics, or commits
- [ ] Review-only findings still carry no executable binding

## Test evidence

<!-- Paste real results: dotnet test counts, flutter analyze/test output,
     build status. "Not run" is an acceptable answer, say why. -->

- `dotnet test native/ClearForge.sln`:
- `flutter analyze` / `flutter test`:
- Windows build:
- New/updated tests:

## Risk & rollback

<!-- What could break? How is it reverted? -->
