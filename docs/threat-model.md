# ClearForge Threat Model (M1)

## Assets
- User files and personal data (the product can destroy them if wrong).
- Windows integrity (system files, update store, recovery data).
- Browser state (credentials, sessions — permanently out of scope).
- Host pipe endpoint (could be abused to delete via confused deputy).

## Threats → mitigations (implemented in M1)

| Threat | Mitigation | Test |
|---|---|---|
| Malicious local client sends path to delete | No path-taking method exists; IDs are session-bound and resolved server-side | `Arbitrary_path_injection_is_impossible`, `Forged_candidate_id_is_rejected` |
| Other-user process connects to pipe | PipeSecurity ACL: full control to creating user SID only; inheritance off | integration: cross-user connect fails (manual/VM) |
| Malformed/oversized frames | 1 MiB cap, JSON parse fail → `malformed_request`, unknown method → `unknown_method` | `Malformed_json_is_rejected`, `Oversized_frame_*`, `Unknown_method_*` |
| Path escapes allowlisted root | canonicalize + `IsContainedIn` per entry; sibling-prefix and `..` rejected | `TrustedRootsTests` |
| Junction/symlink escapes during scan | `AttributesToSkip=ReparsePoint` + per-file reparse check | `Junction_outside_root_is_not_traversed` |
| Scan mutates data | scanner performs reads only | `Scan_is_read_only` (hash+mtime equality) |
| Runaway enumeration | per-rule `max_files_per_scan`, global cap, depth cap, cancellation token | `Scan_limit_is_enforced`, `Cancellation_stops_scan` |
| TOCTOU at clean time | (M2) re-open + revalidate before each op; `Skipped.Changed` semantics | pending M2 |
| Registry "cleaning" | not implemented, forbidden by policy | n/a |

## M7.2 — curated model catalogue & secure install

| Threat | Mitigation | Test |
|---|---|---|
| Malicious/forged model download | Application-bundled catalogue only (no remote catalogue, no user URLs); pinned commit + pinned SHA-256 + exact size verified before install; runtime re-verifies at load | `Wrong_sha_fails_*`, `Bundled_catalogue_*` |
| Redirect to attacker host or downgrade to HTTP | Auto-redirect off; every hop must be HTTPS and inside the catalogue's per-model host allowlist | `Redirect_to_unapproved_host_*`, `Redirect_to_http_*` |
| Oversized/truncated/unexpected content | Content-Length checked against pinned size; hard byte cap while streaming; GGUF magic checked on first bytes | `Oversized_*`, `Body_longer_*`, `Non_gguf_*`, `Truncated_*` |
| Path traversal via model id/name | IDs/filenames come only from the validated catalogue; containment + reparse checks under the model root | `Path_shaped_*`, `Remove_refuses_*` |
| Insecure resume | Resume honoured only for a well-formed 206 with exact offset+total; otherwise clean restart | `Invalid_resume_range_*`, `Server_ignoring_range_*` |
| Deleting a loaded/arbitrary file | Removal only deletes catalogue-named files inside the model root; refuses while an op is active or the file is held open | `Remove_*` |
| Catalogue carries executable content | Strict schema rejects unknown fields; artifacts are data files, never executed or extracted | `Catalogue_cannot_carry_*` |
| `ClearForge.Ai.exe` gaining FS/network power | Worker stays read-only + networkless; downloads live in the engine host's `ClearForge.Models` module | `Models_surface_absent_*` |

## Not yet covered (post-M1)
- Authenticated elevation mechanism design + review before any R2 feature.
- MSIX/installer signature verification.
- Quarantine integrity (MAC or hash manifest) — M2.
- Audit-log tamper resistance — M2.
- Remote catalogue updates: deferred until a signing trust model exists.
