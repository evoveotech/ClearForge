// Client-side mirrors of ClearForge.Contracts. Canonical paths are never
// present here — candidates are identified by opaque session-bound IDs.

enum RiskLevel { low, medium, high, restricted }

RiskLevel _risk(String? s) => switch (s) {
      'low' => RiskLevel.low,
      'medium' => RiskLevel.medium,
      'high' => RiskLevel.high,
      'restricted' => RiskLevel.restricted,
      _ => RiskLevel.restricted,
    };

class ScanCandidate {
  final String id;
  final String ruleId;
  final String category;
  final String displayName;
  final int estimatedBytes;
  final RiskLevel risk;
  final String reason;
  final bool requiresExplicitConfirmation;

  const ScanCandidate({
    required this.id,
    required this.ruleId,
    required this.category,
    required this.displayName,
    required this.estimatedBytes,
    required this.risk,
    required this.reason,
    required this.requiresExplicitConfirmation,
  });

  factory ScanCandidate.fromJson(Map<String, dynamic> j) => ScanCandidate(
        id: j['id'] as String,
        ruleId: j['ruleId'] as String? ?? '',
        category: j['category'] as String? ?? 'unknown',
        displayName: j['displayName'] as String? ?? '',
        estimatedBytes: j['estimatedBytes'] as int? ?? 0,
        risk: _risk(j['risk'] as String?),
        reason: j['reason'] as String? ?? '',
        requiresExplicitConfirmation:
            j['requiresExplicitConfirmation'] as bool? ?? true,
      );
}

class ScanResults {
  final String sessionId;
  final String status; // scanning | completed | cancelled | failed
  final int estimatedBytes;
  final Map<String, int> categories;
  final List<ScanCandidate> candidates;

  const ScanResults({
    required this.sessionId,
    required this.status,
    required this.estimatedBytes,
    required this.categories,
    required this.candidates,
  });

  bool get isTerminal =>
      status == 'completed' ||
      status == 'cancelled' ||
      status == 'failed' ||
      status == 'expired';

  factory ScanResults.fromJson(Map<String, dynamic> j) => ScanResults(
        sessionId: j['sessionId'] as String? ?? '',
        status: j['status'] as String? ?? 'unknown',
        estimatedBytes: j['estimatedBytes'] as int? ?? 0,
        categories: (j['categories'] as Map<String, dynamic>? ?? {})
            .map((k, v) => MapEntry(k, v as int)),
        candidates: (j['candidates'] as List<dynamic>? ?? [])
            .map((c) => ScanCandidate.fromJson(c as Map<String, dynamic>))
            .toList(),
      );
}

/// Structured binding from a recommendation to a real session-bound action.
/// Emitted by the engine only when a completed scan session holds candidates;
/// the UI treats anything else as review-only.
class StorageAction {
  final String kind; // only 'quarantine' is supported in this build
  final String sessionId;
  final String status; // session status at bind time
  final int candidateCount;
  final int eligibleBytes;

  const StorageAction({
    required this.kind,
    required this.sessionId,
    required this.status,
    required this.candidateCount,
    required this.eligibleBytes,
  });

  factory StorageAction.fromJson(Map<String, dynamic> j) => StorageAction(
        kind: j['kind'] as String? ?? '',
        sessionId: j['sessionId'] as String? ?? '',
        status: j['status'] as String? ?? '',
        candidateCount: j['candidateCount'] as int? ?? 0,
        eligibleBytes: j['eligibleBytes'] as int? ?? 0,
      );
}

/// One evidence-backed storage recommendation. `executable` is the single
/// gate the UI uses — forged, stale, or unsupported references never produce
/// an actionable affordance (the engine re-validates regardless).
class StorageRecommendation {
  final String id;
  final String category;
  final String title;
  final String explanation;
  final List<String> evidence;
  final int estimatedLogicalBytes;
  final String confidence;
  final String risk;
  final String reversibility;
  final List<String> preconditions;
  final List<String> skipReasons;
  final String? actionRef;
  final StorageAction? action;

  const StorageRecommendation({
    required this.id,
    required this.category,
    required this.title,
    required this.explanation,
    required this.evidence,
    required this.estimatedLogicalBytes,
    required this.confidence,
    required this.risk,
    required this.reversibility,
    required this.preconditions,
    required this.skipReasons,
    this.actionRef,
    this.action,
  });

  bool get executable =>
      action != null &&
      action!.kind == 'quarantine' &&
      action!.sessionId.isNotEmpty &&
      action!.status == 'completed' &&
      action!.candidateCount > 0 &&
      actionRef == 'session:${action!.sessionId}';

  factory StorageRecommendation.fromJson(Map<String, dynamic> j) =>
      StorageRecommendation(
        id: j['id'] as String? ?? '',
        category: j['category'] as String? ?? 'unknown',
        title: j['title'] as String? ?? '',
        explanation: j['explanation'] as String? ?? '',
        evidence:
            (j['evidence'] as List<dynamic>? ?? []).cast<String>(),
        estimatedLogicalBytes: j['estimatedLogicalBytes'] as int? ?? 0,
        confidence: j['confidence'] as String? ?? 'low',
        risk: j['risk'] as String? ?? 'medium',
        reversibility: j['reversibility'] as String? ?? 'manualonly',
        preconditions:
            (j['preconditions'] as List<dynamic>? ?? []).cast<String>(),
        skipReasons:
            (j['skipReasons'] as List<dynamic>? ?? []).cast<String>(),
        actionRef: j['actionRef'] as String?,
        action: j['action'] is Map<String, dynamic>
            ? StorageAction.fromJson(j['action'] as Map<String, dynamic>)
            : null,
      );
}

/// M8.1: one per-producer status inside a unified Smart Scan run.
class SmartProducerStatus {
  final String producerId;
  final String status; // pending | running | completed | failed | cancelled
  final int findings;
  final String? diagnostic;

  const SmartProducerStatus(this.producerId, this.status, this.findings,
      this.diagnostic);

  factory SmartProducerStatus.fromJson(Map<String, dynamic> j) =>
      SmartProducerStatus(
        j['producerId'] as String? ?? '',
        j['status'] as String? ?? 'pending',
        j['findings'] as int? ?? 0,
        j['diagnostic'] as String?,
      );
}

/// M8.1: one deduplicated finding. Only findings with [actionable] true carry
/// a session binding — and even they can only be executed through the
/// existing preview → approval → commit pipeline. [estimatedLogicalBytes] is
/// a logical estimate, never promised freed space.
class SmartFinding {
  final String id;
  final String producer;
  final String category;
  final String title;
  final String reason;
  final List<String> evidence;
  final String confidence;
  final RiskLevel risk;
  final int estimatedLogicalBytes;
  final String reversibility;
  final bool actionable;
  final String? skipReason;
  final String? sessionId;
  final List<String>? candidateIds;
  final String? duplicateOf;

  /// Live executability of the backing session, computed by the engine at
  /// answer time: 'live' | 'expired' | 'missing' | 'cancelled' | null (no
  /// binding). Anything other than 'live'/null can never commit — the
  /// engine still re-enforces this at preview/commit regardless.
  final String? sessionState;

  const SmartFinding({
    required this.id,
    required this.producer,
    required this.category,
    required this.title,
    required this.reason,
    required this.evidence,
    required this.confidence,
    required this.risk,
    required this.estimatedLogicalBytes,
    required this.reversibility,
    required this.actionable,
    this.skipReason,
    this.sessionId,
    this.candidateIds,
    this.duplicateOf,
    this.sessionState,
  });

  /// Whether this finding may ever be selected for execution. Actionable
  /// findings whose session is expired, evicted, or cancelled are evidence
  /// only — they must rescan before they can act.
  bool get selectable =>
      actionable &&
      sessionState != 'expired' &&
      sessionState != 'missing' &&
      sessionState != 'cancelled';

  /// The backing session can no longer execute — a rescan is required
  /// before any action is possible.
  bool get stale =>
      sessionState == 'expired' ||
      sessionState == 'missing' ||
      sessionState == 'cancelled';

  factory SmartFinding.fromJson(Map<String, dynamic> j) => SmartFinding(
        id: j['id'] as String? ?? '',
        producer: j['producer'] as String? ?? '',
        category: j['category'] as String? ?? 'unknown',
        title: j['title'] as String? ?? '',
        reason: j['reason'] as String? ?? '',
        evidence: (j['evidence'] as List<dynamic>? ?? []).cast<String>(),
        confidence: j['confidence'] as String? ?? 'low',
        risk: _risk(j['risk'] as String?),
        estimatedLogicalBytes: j['estimatedLogicalBytes'] as int? ?? 0,
        reversibility: j['reversibility'] as String? ?? 'manualonly',
        actionable: j['actionable'] as bool? ?? false,
        skipReason: j['skipReason'] as String?,
        sessionId: j['sessionId'] as String?,
        candidateIds: (j['candidateIds'] as List<dynamic>?)?.cast<String>(),
        duplicateOf: j['duplicateOf'] as String?,
        sessionState: j['sessionState'] as String?,
      );
}

/// M8.1: snapshot of a unified scan run — status distinguishes completed,
/// partial, cancelled and failed so partial coverage is never presented as
/// a complete scan.
class SmartScanSnapshot {
  final String smartScanId;
  final String status; // running | completed | partial | cancelled | failed
  final List<SmartProducerStatus> producers;
  final List<SmartFinding> findings;
  final int deduplicated;
  final int estimatedLogicalBytes;

  /// True when the run was aborted by explicit user cancel. A cancelled
  /// run may still carry completed-producer findings (honest partial work)
  /// but its findings must never be auto-selected — the user aborted the
  /// scan, they did not approve its results.
  final bool userCancelled;

  const SmartScanSnapshot({
    required this.smartScanId,
    required this.status,
    required this.producers,
    this.findings = const [],
    this.deduplicated = 0,
    this.estimatedLogicalBytes = 0,
    this.userCancelled = false,
  });

  bool get isRunning => status == 'running';
  bool get isTerminal => !isRunning;

  factory SmartScanSnapshot.fromJson(Map<String, dynamic> j) =>
      SmartScanSnapshot(
        smartScanId: j['smartScanId'] as String? ?? '',
        status: j['status'] as String? ?? 'unknown',
        producers: (j['producers'] as List<dynamic>? ?? [])
            .map((p) =>
                SmartProducerStatus.fromJson(p as Map<String, dynamic>))
            .toList(),
        findings: (j['findings'] as List<dynamic>? ?? [])
            .map((f) => SmartFinding.fromJson(f as Map<String, dynamic>))
            .toList(),
        deduplicated: j['deduplicated'] as int? ?? 0,
        estimatedLogicalBytes: j['estimatedLogicalBytes'] as int? ?? 0,
        userCancelled: j['userCancelled'] as bool? ?? false,
      );
}

/// Human-readable category names for the scan/cleaner surfaces. The engine
/// emits machine category ids; this map is display-only and never affects
/// classification or selection. Unknown categories show their id.
String categoryLabel(String category) => switch (category) {
      'temporary-files' => 'Temporary files',
      'crash-dumps' => 'Crash dumps',
      'browser-cache' => 'Browser cache',
      'duplicates' => 'Duplicate files',
      'thumbnail-cache' => 'Thumbnail cache',
      'gpu-shader-cache' => 'GPU shader cache',
      'package-manager-caches' => 'Package manager caches',
      'windows-error-reports' => 'Windows error reports',
      'startup-items' => 'Startup items',
      _ => category,
    };

String formatBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1 << 20) return '${(bytes / 1024).toStringAsFixed(1)} KB';
  if (bytes < 1 << 30) return '${(bytes / (1 << 20)).toStringAsFixed(1)} MB';
  return '${(bytes / (1 << 30)).toStringAsFixed(2)} GB';
}
