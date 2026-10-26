# Security Policy

ClearForge is a safety-critical Windows application that performs
filesystem operations. We take security reports seriously.

## Reporting a vulnerability

**Do not open a public issue** for security vulnerabilities.

Report privately to Evoveo Tech at **evoveo.tech@gmail.com** with:

- description and impact
- reproduction steps or proof of concept
- affected version/commit

We will acknowledge receipt, investigate, and coordinate disclosure
timing with you.

## Scope notes

- The engine runs unelevated in the user session by design; reports about
  privilege-escalation *requests* are expected design, not bugs — reports
  about privilege escalation *achieved* without user consent are in scope.
- Quarantine integrity (SHA-256 verification, journal) bypasses are
  in-scope and high severity.
- The AI model manager downloads only digest-pinned artifacts over HTTPS;
  supply-chain issues there are in scope.

## Supported versions

Only the latest commit on `main` is supported until the first tagged
release.
