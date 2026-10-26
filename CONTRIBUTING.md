# Contributing to ClearForge

Welcome — the open source community is invited to participate in ClearForge.
Bug reports, feature ideas, documentation fixes, code reviews, and pull
requests are all welcome.

## Ownership and rights

ClearForge is developed by and owned by **Evoveo Tech**. The project is
source-available and community-driven in participation, but all legal
rights, title, and ownership — including every contribution merged into
the codebase — belong exclusively to Evoveo Tech.

By submitting a contribution (pull request, patch, issue attachment, or
any code or documentation you send us), you agree to the **ClearForge
Contributor License Agreement** ([CLA.md](CLA.md)). In short, the CLA means:

- **You assign/broadly license your contribution to Evoveo Tech**, so we
  can use, modify, sublicense, relicense, and commercialize it — including
  under proprietary terms — forever, without needing further permission.
- **Evoveo Tech can take ClearForge to market at any time**, on terms it
  alone decides — including changing or dual-licensing the project —
  without notice to or consent from contributors.
- You confirm your contribution is your original work and that you have
  the right to contribute it.

Please include the line `I have read and agree to the ClearForge CLA
(CLA.md)` in your pull request description.

## Why this model?

This is the same contributor-agreement approach used by projects such as
HashiCorp, MongoDB, Elastic, and Canonical. It lets the community
participate openly while keeping ownership unambiguous — so the project
can be sustained, commercially licensed, and evolved without legal
uncertainty about who owns what.

## How to contribute

1. **Issues first for large changes** — open an issue describing the bug
   or proposal before writing significant code.
2. **Fork and branch** — create a feature branch off `main`.
3. **Match existing conventions** — see `docs/architecture.md`,
   `docs/cleanup-policy.md`, and `docs/threat-model.md`. Safety-critical
   rules (preview → approval → journaled quarantine, fail-closed expiry,
   read-only scanning) are not negotiable.
4. **Tests** — run `dotnet test native\ClearForge.sln` and
   `flutter test` in `apps/desktop_flutter` before submitting.
5. **Keep it focused** — one logical change per pull request.

## Reporting issues

Include: what you observed, what you expected, environment (Windows
version, .NET/Flutter versions), and the smallest repro you can manage.
Never attach real user data, quarantine contents, or journal databases.

## Security reports

Do not open public issues for security vulnerabilities — report them
privately to Evoveo Tech instead.

## License

ClearForge is proprietary software. See [LICENSE](LICENSE) and
[NOTICE](NOTICE). Contributions are governed by [CLA.md](CLA.md).
