# ClearForge — privacy & data handling

## The short version

ClearForge is **local-only**. There is no telemetry, no analytics, no
crash-report upload, no cloud inference, and no account. Nothing you do
in the app leaves your machine through the app's own channels.

## What the app stores locally

| Data | Where | Why |
|---|---|---|
| Cleanup journal (SQLite) | `%LOCALAPPDATA%\ClearForge\journal` | Recovery history, startup reconciliation |
| Quarantined files | `%LOCALAPPDATA%\ClearForge\quarantine` | Reversible cleanup — nothing is permanently deleted |
| Local AI model artifacts | `%LOCALAPPDATA%\ClearForge\models` | Optional, curated, hash-verified local model |
| Preferences / exclusions | `%LOCALAPPDATA%\ClearForge` | App settings |

Nothing is synced, uploaded, or shared. Uninstalling the app leaves this
folder in place on purpose — it is your recovery data. Delete it manually
if you are sure nothing quarantined needs restoring.

## Network access

- The engine/UI communicate over an ACL-protected **local named pipe**
  (`clearforge.engine.v1`, `clearforge.ai.v1`) — no loopback TCP, no
  remote endpoints.
- The optional local AI worker runs an open-weights model entirely
  on-device. The model cannot emit paths, commands, or mutations — it
  produces validated constraints the deterministic engine interprets.
- Model download (only when you explicitly install a curated model)
  fetches from the catalog URL — the only network call the product makes
  today, and it is user-initiated.

## What the app never does

- Permanently deletes files — every cleanup is quarantined and restorable
- Moves or modifies anything without preview → explicit approval →
  per-item revalidation
- Uploads file paths, scan results, or usage data
- Bundles adware, crypto miners, browser hijackers, or "partner offers"
- Makes "performance boost" or "100% safe" claims

## Security reporting

Found a vulnerability? Do **not** open a public issue — report it via
GitHub Security Advisories at
`github.com/evoveotech/ClearForge/security/advisories`.
