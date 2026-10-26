// Pure approval-safety helpers for the plan review flow. Kept free of the
// engine client so they are unit-testable without a host.

/// An approved plan action identity: recommendation id + bound session id.
/// Two actions with the same title but different sessions are NOT the same
/// action — a fresh session means fresh candidates and fresh bytes.
String actionIdentity(Map<String, dynamic> action) {
  final a = action['action'] as Map<String, dynamic>?;
  return '${action['id']}@${a?['sessionId']}';
}

/// True when every approved action is present, identical (id + session), in
/// the freshly replanned selection — i.e. a server-side replan could not have
/// silently expanded or mutated the approved set.
bool approvedSetIntact(
    List<Map<String, dynamic>> approved, List<Map<String, dynamic>> replanned) {
  final fresh = replanned.map(actionIdentity).toSet();
  return approved.isNotEmpty && approved.every((a) => fresh.contains(actionIdentity(a)));
}

/// Batch-size confirmation threshold: beyond this, a single approval moves
/// more files than a person can meaningfully review on one screen. Chosen
/// from observed data — real browser-cache batches reach ~2,800 files — and
/// deliberately conservative. Justification, not arbitrary: a batch this
/// large warrants one more explicit click.
const int kBatchConfirmCandidates = 500;

/// Byte threshold for the same confirmation: >1 GiB of estimated logical
/// movement in one approval is large enough to deserve a second look.
const int kBatchConfirmBytes = 1024 * 1024 * 1024;

/// Whether the approved batch needs an additional confirmation step.
bool requiresBatchConfirm(int totalCandidates, int totalBytes) =>
    totalCandidates > kBatchConfirmCandidates || totalBytes > kBatchConfirmBytes;
