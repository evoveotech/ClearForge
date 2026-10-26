import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';

import '../ai/ai_worker.dart';
import '../ipc/engine_pipe_client.dart';
import '../models/ai_models.dart';
import '../models/scan_models.dart';

enum EngineStatus { disconnected, starting, ready, scanning, executing, error }

/// One per-item cleanup outcome from the engine.
class CleanupItemOutcome {
  final String candidateId;
  final String status;
  final int bytesReclaimed;
  final String? errorCode;
  const CleanupItemOutcome(
      this.candidateId, this.status, this.bytesReclaimed, this.errorCode);
}

/// A journaled quarantine/history entry (paths never leave the engine).
/// [integrity] is the engine's provenance marker: 'verified'/'restored' mean
/// content was cryptographically confirmed; 'legacy'/'legacy_restored' mean
/// the record predates hashing and is explicitly NOT verified.
class JournalEntry {
  final String operationId;
  final String sessionId;
  final String candidateId;
  final String? category;
  final int bytes;
  final String state;
  final String? errorCode;
  final String? displayName;
  final String updatedUtc;
  final String? integrity;
  const JournalEntry(this.operationId, this.sessionId, this.candidateId,
      this.category, this.bytes, this.state, this.errorCode, this.displayName,
      this.updatedUtc, [this.integrity]);

  factory JournalEntry.fromJson(Map<String, dynamic> j) => JournalEntry(
        j['operationId'] as String? ?? '',
        j['sessionId'] as String? ?? '',
        j['candidateId'] as String? ?? '',
        j['category'] as String?,
        j['bytes'] as int? ?? 0,
        j['state'] as String? ?? 'unknown',
        j['errorCode'] as String?,
        j['displayName'] as String?,
        j['updatedUtc'] as String? ?? '',
        j['integrity'] as String?,
      );
}

/// Owns the engine process, the pipe connection and the active scan session.
/// All heavy lifting happens engine-side; this class only carries validated
/// responses into UI state.
class EngineController extends ChangeNotifier {
  static const defaultPipeName = 'clearforge.engine.v1';

  /// The pipe this controller talks to. Tests must use a unique, test-owned
  /// pipe name so a stale production host can never answer the connection.
  final String pipeName;

  /// When true, connect() verifies that health.check's reported process id
  /// equals the pid of the host process this controller spawned — proving the
  /// connected instance is the launched one, not a stale host on the pipe.
  final bool requireProcessMatch;

  EngineController(
      {this.pipeName = defaultPipeName, this.requireProcessMatch = false});

  Process? _engine;
  EngineClient? _client;
  Timer? _pollTimer;

  EngineStatus status = EngineStatus.disconnected;
  String? engineVersion;

  /// Process id reported by the connected host in health.check.
  int? enginePid;

  /// Pid of the host process this controller spawned (null if not spawned).
  int? get spawnedPid => _engine?.pid;

  String? errorMessage;
  String? sessionId;
  ScanResults? results;

  /// Candidate IDs selected by the user in the review list.
  final Set<String> selected = {};

  /// Per-item outcomes from the last commit; null until a commit completes.
  List<CleanupItemOutcome>? lastCommitResults;

  /// Journal history for the quarantine/restore view.
  List<JournalEntry> history = [];

  Future<void> connect() async {
    status = EngineStatus.starting;
    errorMessage = null;
    notifyListeners();
    try {
      _engine ??= await EngineProcess.spawn(pipeName: pipeName);
      // The host binds its pipe asynchronously after process start; retry the
      // handshake until it answers instead of failing on first contact.
      Object? lastError = 'no response';
      for (var attempt = 0; attempt < 40; attempt++) {
        _client = await EngineClient.connect(pipeName);
        final health = await _client!
            .call('health.check')
            .timeout(const Duration(seconds: 2));
        if (health['error'] == null) {
          engineVersion = health['result']?['engineVersion'] as String?;
          enginePid = health['result']?['pid'] as int?;
          if (requireProcessMatch && enginePid != spawnedPid) {
            throw StateError('engine instance mismatch: pipe answered pid '
                '$enginePid but spawned host pid is $spawnedPid — a stale '
                'host may own this pipe');
          }
          status = EngineStatus.ready;
          unawaited(refreshHistory());
          unawaited(loadModelCatalog());
          notifyListeners();
          return;
        }
        lastError = health['error'];
        await _client?.dispose();
        _client = null;
        await Future<void>.delayed(const Duration(milliseconds: 250));
      }
      throw StateError('engine health check failed: $lastError');
    } catch (e) {
      status = EngineStatus.error;
      errorMessage = '$e';
      // Drop stale handles so Retry can actually respawn a dead engine.
      await _client?.dispose();
      _client = null;
      _engine?.kill();
      _engine = null;
    }
    notifyListeners();
  }

  Future<void> startScan() async {
    if (_client == null) return;
    status = EngineStatus.scanning;
    results = null;
    selected.clear();
    lastCommitResults = null;
    notifyListeners();

    final res = await _client!.call('scan.start');
    if (res['error'] != null) {
      status = EngineStatus.error;
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return;
    }
    sessionId = res['result']?['sessionId'] as String?;
    _pollTimer = Timer.periodic(const Duration(milliseconds: 250), (_) => _poll());
    unawaited(_poll());
  }

  /// Poll scan progress with the lightweight status call — the full
  /// candidate payload is fetched exactly once at terminal. Large scans
  /// (M8.2 cache categories can reach the 50k-per-rule caps) would otherwise
  /// re-serialize tens of thousands of records every 250ms.
  Future<void> _poll() async {
    if (_client == null || sessionId == null) return;
    final st = await _client!
        .call('operation.getStatus', {'sessionId': sessionId});
    if (st['error'] != null) {
      _pollTimer?.cancel();
      status = EngineStatus.error;
      errorMessage = st['error']['message'] as String?;
      notifyListeners();
      return;
    }
    final stRes = st['result'] as Map<String, dynamic>?;
    if (stRes == null) return;
    final live = stRes['status'] as String? ?? 'unknown';
    if (live == 'scanning') {
      results = ScanResults(
          sessionId: sessionId!,
          status: live,
          estimatedBytes: stRes['estimatedBytes'] as int? ?? 0,
          categories: const {},
          candidates: const []);
      notifyListeners();
      return;
    }
    // Cancel BEFORE fetching the full payload: a large candidate set takes
    // seconds to fetch and decode, and every extra tick while that fetch is
    // in flight would queue another identical multi-MB request.
    _pollTimer?.cancel();
    final res = await _client!
        .call('scan.getResults', {'sessionId': sessionId});
    if (res['error'] != null) {
      _pollTimer?.cancel();
      status = EngineStatus.error;
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return;
    }
    final payload = res['result'] as Map<String, dynamic>?;
    if (payload == null) return;
    results = ScanResults.fromJson(payload);
    if (results!.isTerminal) {
      _pollTimer?.cancel();
      status = EngineStatus.ready;
      // Pre-select low-risk candidates; medium/high stay opt-in per policy.
      for (final c in results!.candidates) {
        if (c.risk == RiskLevel.low) selected.add(c.id);
      }
    }
    notifyListeners();
  }

  void toggleSelected(String id, bool value) {
    if (value) {
      selected.add(id);
    } else {
      selected.remove(id);
    }
    notifyListeners();
  }

  Future<void> cancelScan() async {
    if (_client == null || sessionId == null) return;
    await _client!.call('scan.cancel', {'sessionId': sessionId});
  }

  /// Cancel an in-flight commit on the active session. Cancellation is
  /// cooperative between items: completed moves stay journaled and
  /// restorable; remaining items report 'cancelled'.
  Future<void> cancelExecution({String? forSession}) async {
    final sid = forSession ?? sessionId;
    if (_client == null || sid == null) return;
    await _client!.call('cleanup.cancel', {'sessionId': sid});
  }

  /// Largest candidate-id batch per preview/commit pair. Request frames are
  /// hard-capped at 1 MiB — a batch beyond ~20k ids exceeds it and the host
  /// tears down the connection, so large sets commit in bounded chunks. Each
  /// chunk is still a complete preview → approval → commit cycle; the
  /// engine's approval semantics are unchanged.
  static const int _commitChunkSize = 8000;

  /// Preview then commit [ids] for [sessionId] in bounded chunks. Per-item
  /// outcomes land in [outcomes]; cleaned ids accumulate in [cleanedIds].
  /// A session-level failure (expired/missing/refused) stops that session's
  /// remaining chunks — already-committed chunks stay journaled and are
  /// reported honestly; nothing is silently resubmitted.
  Future<void> _commitSessionChunked(
      String sessionId, List<String> ids, List<CleanupItemOutcome> outcomes,
      {Set<String>? cleanedIds,
      Set<String>? staleSessions,
      void Function(int?)? onDelta}) async {
    for (var i = 0; i < ids.length; i += _commitChunkSize) {
      final chunk = ids.sublist(i, (i + _commitChunkSize).clamp(0, ids.length));
      final preview = await _client!.call('cleanup.preview',
          {'sessionId': sessionId, 'candidateIds': chunk});
      if (preview['error'] != null) {
        final code = preview['error']['code'] as String? ?? 'preview_failed';
        if (code == 'session_expired' || code == 'session_not_found') {
          staleSessions?.add(sessionId);
        }
        outcomes.add(CleanupItemOutcome(sessionId, 'error', 0, code));
        return;
      }
      final res = await _client!.call('cleanup.commit',
          {'sessionId': sessionId, 'candidateIds': chunk});
      if (res['error'] != null) {
        final code = res['error']['code'] as String? ?? 'commit_failed';
        if (code == 'session_expired' || code == 'session_not_found') {
          staleSessions?.add(sessionId);
        }
        outcomes.add(CleanupItemOutcome(sessionId, 'error', 0, code));
        return;
      }
      onDelta?.call(res['result']?['freeSpaceDeltaBytes'] as int?);
      for (final i2 in (res['result']?['items'] as List<dynamic>? ?? [])) {
        final status = i2['status'] as String? ?? 'unknown';
        final cid = i2['candidateId'] as String? ?? '';
        if (status == 'cleaned' || status == 'already_quarantined') {
          cleanedIds?.add(cid);
        }
        outcomes.add(CleanupItemOutcome(cid, status,
            i2['bytesReclaimed'] as int? ?? 0,
            i2['errorCode'] as String?));
      }
    }
  }

  /// Commit the selected candidates to quarantine. Preview is recorded
  /// engine-side first — the commit is refused without that approval.
  /// Returns when the engine has processed every item — outcomes are
  /// per-item, never silent.
  Future<bool> commitSelected() async {
    if (_client == null || sessionId == null || selected.isEmpty) return false;
    status = EngineStatus.executing;
    notifyListeners();
    final outcomes = <CleanupItemOutcome>[];
    await _commitSessionChunked(sessionId!, selected.toList(), outcomes);
    status = EngineStatus.ready;
    lastCommitResults = outcomes;
    notifyListeners();
    unawaited(refreshHistory());
    return true;
  }

  Future<void> refreshHistory() async {
    if (_client == null) return;
    final res = await _client!.call('history.get');
    final entries = res['result']?['entries'] as List<dynamic>? ?? [];
    history = entries
        .map((e) => JournalEntry.fromJson(e as Map<String, dynamic>))
        .toList();
    notifyListeners();
  }

  /// Restore quarantined entries by operation ID. Never overwrites.
  Future<List<CleanupItemOutcome>> restore(List<String> operationIds) async {
    if (_client == null) return const [];
    final res = await _client!
        .call('cleanup.restore', {'operationIds': operationIds});
    final items = (res['result']?['items'] as List<dynamic>? ?? []);
    final outcomes = items
        .map((i) => CleanupItemOutcome(
              i['operationId'] as String? ?? '',
              i['status'] as String? ?? 'unknown',
              0,
              i['errorCode'] as String?,
            ))
        .toList();
    unawaited(refreshHistory());
    return outcomes;
  }

  // --- M8.1 Smart Scan state ---
  SmartScanSnapshot? smartScan;
  Timer? _smartPollTimer;

  /// Finding ids the user explicitly selected. Review-only findings can
  /// never enter this set — there is no executable binding behind them.
  final Set<String> smartSelected = {};

  /// Sessions proven dead at commit time (expired/evicted/cancelled).
  /// Findings bound to them are unselectable and a rescan banner is shown;
  /// they can never mutate anything.
  final Set<String> smartStaleSessions = {};

  /// Finding ids whose approved candidates all reached a quarantined state.
  /// They stay visible as completed work and can never be re-selected —
  /// retrying can never re-submit or widen an approved set.
  final Set<String> smartCommitted = {};

  /// Per-item outcomes from the last smart commit batch.
  List<CleanupItemOutcome>? smartCommitResults;
  bool smartExecuting = false;

  /// True when any finding's backing session can no longer execute.
  bool get smartNeedsRescan =>
      smartScan?.findings.any((f) =>
          f.stale || (f.sessionId != null && smartStaleSessions.contains(f.sessionId))) ??
      false;

  bool _smartSelectable(SmartFinding f) =>
      f.selectable &&
      !smartCommitted.contains(f.id) &&
      (f.sessionId == null || !smartStaleSessions.contains(f.sessionId));

  Future<void> startSmartScan() async {
    if (_client == null) return;
    _staleRefreshTimer?.cancel();
    status = EngineStatus.scanning;
    smartScan = null;
    smartSelected.clear();
    smartCommitted.clear();
    smartStaleSessions.clear();
    smartCommitResults = null;
    notifyListeners();
    final res = await _client!.call('smartscan.start');
    if (res['error'] != null) {
      status = EngineStatus.error;
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return;
    }
    final sid = res['result']?['smartScanId'] as String?;
    if (sid == null) return;
    _smartPollTimer?.cancel();
    _smartPollTimer = Timer.periodic(
        const Duration(milliseconds: 400), (_) => _smartPoll(sid));
    unawaited(_smartPoll(sid));
  }

  Future<void> _smartPoll(String smartScanId) async {
    if (_client == null) return;
    final res = await _client!
        .call('smartscan.status', {'smartScanId': smartScanId});
    if (res['error'] != null) {
      _smartPollTimer?.cancel();
      status = EngineStatus.error;
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return;
    }
    final st = SmartScanSnapshot.fromJson(
        (res['result'] as Map<String, dynamic>?) ?? {});
    final keep = smartScan?.findings ?? const <SmartFinding>[];
    smartScan = SmartScanSnapshot(
      smartScanId: st.smartScanId,
      status: st.status,
      producers: st.producers,
      findings: keep.isEmpty ? st.findings : keep,
      deduplicated: st.deduplicated,
      estimatedLogicalBytes: st.estimatedLogicalBytes,
      userCancelled: st.userCancelled,
    );
    if (!st.isRunning) {
      _smartPollTimer?.cancel();
      status = EngineStatus.ready;
      final full = await _client!
          .call('smartscan.results', {'smartScanId': smartScanId});
      if (full['error'] == null) {
        smartScan = SmartScanSnapshot.fromJson(
            (full['result'] as Map<String, dynamic>?) ?? {});
        // Only low-risk actionable findings pre-select — and never on a run
        // the user aborted. A cancelled scan's findings are evidence to
        // review, not a pre-approved work list.
        if (!smartScan!.userCancelled) {
          for (final f in smartScan!.findings) {
            if (_smartSelectable(f) && f.risk == RiskLevel.low) {
              smartSelected.add(f.id);
            }
          }
        }
        _scheduleStaleRefresh(smartScanId);
      }
    }
    notifyListeners();
  }

  /// Slow staleness refresh: a displayed snapshot's sessionState is computed
  /// at fetch time, but sessions expire while results sit on screen. A 30 s
  /// re-fetch keeps the expired/rescan state honest without churning the
  /// engine; it stops when nothing actionable remains or a new scan starts.
  Timer? _staleRefreshTimer;

  void _scheduleStaleRefresh(String smartScanId) {
    _staleRefreshTimer?.cancel();
    _staleRefreshTimer = Timer.periodic(const Duration(seconds: 30),
        (_) => unawaited(_refreshSmartResults(smartScanId)));
  }

  Future<void> _refreshSmartResults(String smartScanId) async {
    if (_client == null || smartExecuting) return;
    if (smartScan?.smartScanId != smartScanId) return;
    final res = await _client!
        .call('smartscan.results', {'smartScanId': smartScanId});
    if (res['error'] != null) {
      // The run itself was evicted: every bound session is unreachable.
      if (res['error']?['code'] == 'session_not_found') {
        for (final f in smartScan?.findings ?? const <SmartFinding>[]) {
          if (f.sessionId != null) smartStaleSessions.add(f.sessionId!);
        }
        _staleRefreshTimer?.cancel();
        notifyListeners();
      }
      return;
    }
    final fresh = SmartScanSnapshot.fromJson(
        (res['result'] as Map<String, dynamic>?) ?? {});
    if (!fresh.isTerminal) return;
    smartScan = fresh;
    // Prune selections whose backing session went stale — visible, never
    // silent: the rescan banner and the finding's stale icon say why.
    smartSelected.removeWhere((id) => !smartScan!.findings
        .any((f) => f.id == id && _smartSelectable(f)));
    if (!smartScan!.findings.any((f) => f.actionable)) {
      _staleRefreshTimer?.cancel();
    }
    notifyListeners();
  }

  Future<void> cancelSmartScan() async {
    if (_client == null || smartScan == null) return;
    await _client!.call(
        'smartscan.cancel', {'smartScanId': smartScan!.smartScanId});
  }

  void toggleSmartSelected(String id, bool value) {
    final f = smartScan?.findings.firstWhere(
        (x) => x.id == id,
        orElse: () => const SmartFinding(
            id: '', producer: '', category: '', title: '', reason: '',
            evidence: [], confidence: 'low', risk: RiskLevel.restricted,
            estimatedLogicalBytes: 0, reversibility: 'manualonly',
            actionable: false));
    if (f == null || !_smartSelectable(f)) return; // review-only/stale/done
    if (value) {
      smartSelected.add(id);
    } else {
      smartSelected.remove(id);
    }
    notifyListeners();
  }

  /// Read-only details behind a finding: the session's public candidate
  /// projection filtered to this finding's approved ids. Never records a
  /// preview or any other engine-side state. Returns null when the session
  /// is gone — the caller then knows the finding needs a rescan.
  Future<List<ScanCandidate>?> smartFindingDetails(SmartFinding f) async {
    if (_client == null || f.sessionId == null) return null;
    final res = await _client!
        .call('scan.getResults', {'sessionId': f.sessionId});
    if (res['error'] != null) {
      if (res['error']?['code'] == 'session_not_found') {
        smartStaleSessions.add(f.sessionId!);
        notifyListeners();
      }
      return null;
    }
    final results = ScanResults.fromJson(
        (res['result'] as Map<String, dynamic>?) ?? {});
    final wanted = f.candidateIds?.toSet();
    if (wanted == null) return results.candidates;
    return results.candidates.where((c) => wanted.contains(c.id)).toList();
  }

  /// Commit the selected findings. Each finding's binding is grouped by its
  /// child session; every group goes through cleanup.preview → explicit
  /// engine approval → cleanup.commit — the same authoritative pipeline as
  /// everywhere else, no second approval mechanism. The set submitted to
  /// commit is exactly the set previewed; nothing outside the approved
  /// ids is ever sent.
  Future<void> commitSmartScan() async {
    if (_client == null || smartScan == null || smartSelected.isEmpty) return;
    final chosen = smartScan!.findings
        .where((f) => _smartSelectable(f) && smartSelected.contains(f.id))
        .toList();
    // Selected findings that can no longer execute are reported, never
    // silently dropped or silently replaced.
    final staleChosen = smartScan!.findings
        .where((f) => smartSelected.contains(f.id) && !_smartSelectable(f))
        .toList();

    smartExecuting = true;
    notifyListeners();
    final outcomes = <CleanupItemOutcome>[];
    for (final f in staleChosen) {
      final dead = f.sessionId != null &&
          (f.stale || smartStaleSessions.contains(f.sessionId));
      if (dead) {
        if (f.sessionId != null) smartStaleSessions.add(f.sessionId!);
        outcomes.add(CleanupItemOutcome(
            f.id, 'error', 0, 'session_expired'));
      } else {
        outcomes.add(CleanupItemOutcome(
            f.id, 'error', 0, 'no_executable_binding'));
      }
      smartSelected.remove(f.id);
    }
    // Group candidate ids per owning session — one approval per session.
    final bySession = <String, List<String>>{};
    for (final f in chosen) {
      if (f.sessionId == null || f.candidateIds == null) {
        outcomes.add(CleanupItemOutcome(
            f.id, 'error', 0, 'no_executable_binding'));
        continue;
      }
      bySession.putIfAbsent(f.sessionId!, () => []).addAll(f.candidateIds!);
    }
    final cleanedIds = <String>{};
    for (final entry in bySession.entries) {
      await _commitSessionChunked(entry.key, entry.value, outcomes,
          cleanedIds: cleanedIds, staleSessions: smartStaleSessions);
    }
    // A finding is "done" only when every one of its approved candidates
    // reached a quarantined state — partial outcomes stay visible as such.
    for (final f in chosen) {
      final ids = f.candidateIds;
      if (ids != null && ids.isNotEmpty &&
          ids.every(cleanedIds.contains)) {
        smartCommitted.add(f.id);
        smartSelected.remove(f.id);
      }
    }
    smartCommitResults = outcomes;
    smartExecuting = false;
    notifyListeners();
    unawaited(refreshHistory());
  }

  // --- M3 feature state ---
  List<Map<String, dynamic>> duplicateGroups = [];
  List<Map<String, dynamic>> startupEntries = [];
  List<Map<String, dynamic>> privacyProfiles = [];

  /// Duplicates: named scopes only — the engine refuses unknown ids.
  Future<void> startDuplicatesScan(List<String> scopeIds) async {
    if (_client == null) return;
    status = EngineStatus.scanning;
    duplicateGroups = [];
    notifyListeners();
    final res =
        await _client!.call('duplicates.start', {'scopeIds': scopeIds});
    if (res['error'] != null) {
      errorMessage = res['error']['message'] as String?;
      status = EngineStatus.error;
      notifyListeners();
      return;
    }
    final sid = res['result']?['sessionId'] as String?;
    if (sid == null) return;
    // Poll until the scan completes, then fetch groups.
    for (var i = 0; i < 200; i++) {
      await Future.delayed(const Duration(milliseconds: 250));
      final st = await _client!.call('operation.getStatus', {'sessionId': sid});
      if (st['result']?['status'] != 'scanning') break;
    }
    final g =
        await _client!.call('duplicates.groups', {'sessionId': sid});
    duplicateGroups = (g['result']?['groups'] as List<dynamic>? ?? [])
        .cast<Map<String, dynamic>>();
    status = EngineStatus.ready;
    notifyListeners();
  }

  /// Quarantine chosen duplicate copies through the normal
  /// preview → commit path. The engine refuses the commit without a
  /// matching preview approval, so the preview is mandatory, not cosmetic.
  Future<bool> commitDuplicateCandidates(
      String sessionId, List<String> candidateIds) async {
    if (_client == null || candidateIds.isEmpty) return false;
    final outcomes = <CleanupItemOutcome>[];
    await _commitSessionChunked(sessionId, candidateIds, outcomes);
    unawaited(refreshHistory());
    return outcomes.isNotEmpty &&
        outcomes.every((o) => o.status != 'error');
  }

  Future<void> loadStartup() async {
    if (_client == null) return;
    final res = await _client!.call('startup.list');
    startupEntries = (res['result']?['entries'] as List<dynamic>? ?? [])
        .cast<Map<String, dynamic>>();
    notifyListeners();
  }

  /// Browser cache scan: produces a normal scan session — candidates flow
  /// through the shared preview/commit/quarantine pipeline.
  Future<void> startPrivacyCacheScan() async {
    if (_client == null) return;
    status = EngineStatus.scanning;
    results = null;
    selected.clear();
    lastCommitResults = null;
    notifyListeners();
    final res = await _client!.call('privacy.scanCache');
    if (res['error'] != null) {
      status = EngineStatus.error;
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return;
    }
    sessionId = res['result']?['sessionId'] as String?;
    _pollTimer = Timer.periodic(const Duration(milliseconds: 250), (_) => _poll());
    unawaited(_poll());
  }

  // --- M4 Smart Storage ---
  Map<String, dynamic>? storageInventory;   // {totalLogicalBytes, partial, scopes}
  List<StorageRecommendation> recommendations = [];
  Map<String, dynamic>? storagePlanResult;  // {likelyAchievable, ...} or null
  bool storageScanning = false;

  Future<void> loadStorageIntelligence() async {
    if (_client == null) return;
    storageScanning = true;
    errorMessage = null;
    notifyListeners();
    try {
      final inv = await _client!.call('storage.inventory');
      if (inv['error'] != null) {
        errorMessage = inv['error']['message'] as String?;
        storageScanning = false;
        notifyListeners();
        return;
      }
      storageInventory = inv['result'] as Map<String, dynamic>?;

      final rec = await _client!.call('storage.recommend');
      recommendations = rec['error'] == null
          ? (rec['result']?['recommendations'] as List<dynamic>? ?? [])
              .map((r) =>
                  StorageRecommendation.fromJson(r as Map<String, dynamic>))
              .toList()
          : [];
    } catch (e) {
      errorMessage = '$e';
    }
    storageScanning = false;
    notifyListeners();
  }

  /// Fetch the live candidates behind an executable recommendation.
  /// Returns null when the bound session is gone, expired, or not completed —
  /// the UI must then drop the action and offer a rescan.
  Future<ScanResults?> storageActionCandidates(StorageAction action) async {
    if (_client == null || action.sessionId.isEmpty) return null;
    final st = await _client!
        .call('operation.getStatus', {'sessionId': action.sessionId});
    if (st['error'] != null || st['result']?['status'] != 'completed') {
      return null;
    }
    final res = await _client!
        .call('scan.getResults', {'sessionId': action.sessionId});
    final payload = res['result'] as Map<String, dynamic>?;
    if (res['error'] != null || payload == null) return null;
    return ScanResults.fromJson(payload);
  }

  /// Engine-side preview revalidation for an action's candidates. Returns the
  /// approved byte total, or null when any id failed validation.
  Future<int?> previewStorageAction(
      String sessionId, List<String> candidateIds) async {
    if (_client == null || candidateIds.isEmpty) return null;
    final res = await _client!.call('cleanup.preview',
        {'sessionId': sessionId, 'candidateIds': candidateIds});
    if (res['error'] != null) return null;
    return res['result']?['estimatedBytes'] as int?;
  }

  /// Full preview payload — includes per-candidate eligibility probes
  /// (eligible/locked/changed/missing/elevation_required) so the approval UI
  /// can show expected skips before anything is approved. Read-only.
  Future<Map<String, dynamic>?> previewStorageActionDetailed(
      String sessionId, List<String> candidateIds) async {
    if (_client == null || candidateIds.isEmpty) return null;
    final res = await _client!.call('cleanup.preview',
        {'sessionId': sessionId, 'candidateIds': candidateIds});
    if (res['error'] != null) return null;
    return res['result'] as Map<String, dynamic>?;
  }

  /// Commit an action's candidates through the existing journaled quarantine
  /// path (preview → approval → commit per bounded chunk). Per-item outcomes
  /// land in [lastCommitResults]; the engine still revalidates every id
  /// against the session before moving anything.
  Future<bool> commitStorageAction(
      String sessionId, List<String> candidateIds) async {
    if (_client == null || candidateIds.isEmpty) return false;
    status = EngineStatus.executing;
    notifyListeners();
    final outcomes = <CleanupItemOutcome>[];
    await _commitSessionChunked(sessionId, candidateIds, outcomes,
        onDelta: (d) => freeSpaceDeltaBytes = d);
    status = EngineStatus.ready;
    final failed = outcomes.isEmpty ||
        outcomes.any((o) => o.status == 'error');
    if (failed) {
      errorMessage = outcomes
          .where((o) => o.status == 'error')
          .map((o) => o.errorCode)
          .firstWhere((c) => c != null, orElse: () => 'commit_failed');
    }
    lastCommitResults = outcomes;
    notifyListeners();
    unawaited(refreshHistory());
    return !failed;
  }

  /// Measured volume free-space delta from the last commit, when the engine
  /// could measure it. Informational only — same-volume quarantine moves are
  /// not expected to free space.
  int? freeSpaceDeltaBytes;

  /// Goal planner — bytes from user-entered GB, plus optional user
  /// constraints (rejected recommendation IDs / excluded categories and
  /// scopes). Constraints only narrow selection; the engine re-validates.
  Future<Map<String, dynamic>?> planStorageGoal(double goalGb,
      {List<String>? excludeIds,
      List<String>? excludeCategories,
      List<String>? excludeScopes}) async {
    if (_client == null || goalGb <= 0) return null;
    final constraints = <String, dynamic>{
      if (excludeIds != null && excludeIds.isNotEmpty) 'excludeIds': excludeIds,
      if (excludeCategories != null && excludeCategories.isNotEmpty)
        'excludeCategories': excludeCategories,
      if (excludeScopes != null && excludeScopes.isNotEmpty)
        'excludeScopes': excludeScopes,
    };
    final res = await _client!.call('storage.plan', {
      'goalBytes': (goalGb * 1024 * 1024 * 1024).round(),
      if (constraints.isNotEmpty) 'constraints': constraints,
    });
    if (res['error'] != null) {
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return null;
    }
    storagePlanResult = res['result'] as Map<String, dynamic>?;
    notifyListeners();
    return storagePlanResult;
  }

  /// Per-action outcome of an executed plan step.
  List<Map<String, dynamic>>? planExecutionOutcomes;

  /// Execute approved plan actions sequentially through the existing
  /// preview → revalidate → journaled-commit pipeline. Each step re-fetches
  /// live candidates, previews, and commits — a stale or unavailable action
  /// is reported, never silently skipped. Nothing executes beyond the
  /// approved list.
  Future<List<Map<String, dynamic>>> executePlanActions(
      List<StorageRecommendation> approved) async {
    final outcomes = <Map<String, dynamic>>[];
    for (final rec in approved) {
      final action = rec.action;
      if (action == null) {
        outcomes.add({'id': rec.id, 'status': 'skipped', 'reason': 'review-only'});
        continue;
      }
      final results = await storageActionCandidates(action);
      if (results == null) {
        outcomes.add({
          'id': rec.id,
          'status': 'unavailable',
          'reason': 'session stale or expired'
        });
        continue;
      }
      final ids = results.candidates.map((c) => c.id).toList();
      // Preview+commit through the chunked helper — the per-chunk preview is
      // the mandatory approval gate, and a failed preview/commit surfaces as
      // an 'error' item outcome rather than an implicit skip.
      status = EngineStatus.executing;
      notifyListeners();
      final actionOutcomes = <CleanupItemOutcome>[];
      await _commitSessionChunked(action.sessionId, ids, actionOutcomes,
          onDelta: (d) => freeSpaceDeltaBytes = d);
      status = EngineStatus.ready;
      lastCommitResults = actionOutcomes;
      final failed =
          actionOutcomes.isEmpty || actionOutcomes.any((o) => o.status == 'error');
      outcomes.add({
        'id': rec.id,
        'status': failed ? 'failed' : 'committed',
        'items': actionOutcomes
            .map((i) => {
                  'candidateId': i.candidateId,
                  'status': i.status,
                  'errorCode': i.errorCode,
                  'bytesReclaimed': i.bytesReclaimed,
                })
            .toList(),
      });
    }
    planExecutionOutcomes = outcomes;
    notifyListeners();
    return outcomes;
  }

  Future<void> loadPrivacy() async {
    if (_client == null) return;
    final res = await _client!.call('privacy.detect');
    privacyProfiles = (res['result']?['profiles'] as List<dynamic>? ?? [])
        .cast<Map<String, dynamic>>();
    notifyListeners();
  }

  // --- M7.2 curated AI model catalogue + install lifecycle ---

  /// Engine-published catalogue entries with live lifecycle state.
  List<AiModelInfo> aiModels = [];
  bool modelsLoading = false;
  Timer? _modelPollTimer;

  /// A model is usable only when the engine reports it digest-verified ready
  /// AND the artifact is actually present under the model root.
  bool get modelReady => readyModel != null;

  /// The AI assist affordance: a digest-verified ready model, or — for the
  /// pre-catalogue dev flow — a model file already present on disk that the
  /// worker will still hash-verify before loading.
  bool get aiAssistAvailable => modelReady || ai.modelPresent;

  AiModelInfo? get readyModel {
    for (final m in aiModels) {
      if (m.status?.state == AiInstallState.ready) return m;
    }
    return null;
  }

  /// Fetch the curated catalogue and refresh each model's on-disk state.
  Future<void> loadModelCatalog() async {
    if (_client == null) return;
    modelsLoading = true;
    notifyListeners();
    try {
      final res = await _client!.call('models.catalog');
      if (res['error'] != null) return;
      aiModels = (res['result']?['models'] as List<dynamic>? ?? [])
          .map((m) => AiModelInfo.fromJson(m as Map<String, dynamic>))
          .toList();
      _syncWorkerModel();
    } finally {
      modelsLoading = false;
      notifyListeners();
    }
  }

  /// Point the AI worker at the installed curated artifact — catalogue
  /// filenames only, never arbitrary paths.
  void _syncWorkerModel() {
    final ready = readyModel;
    if (ready != null) {
      ai.modelFileName = ready.fileName;
      ai.modelId = ready.id;
    }
  }

  /// Merge live per-model state into the catalogue view.
  void _mergeModelStates(Map<String, dynamic>? result) {
    final states = (result?['models'] as List<dynamic>? ?? [])
        .map((s) => AiModelStatus.fromJson(s as Map<String, dynamic>))
        .toList();
    final byId = {for (final s in states) s.modelId: s};
    aiModels = aiModels.map((m) => m.withStatus(byId[m.id])).toList();
    _syncWorkerModel();
    notifyListeners();
  }

  /// Explicit user-initiated download+install. Polls status while active;
  /// nothing here runs automatically.
  Future<bool> installModel(String id) async {
    if (_client == null) return false;
    final res = await _client!.call('models.install', {'modelId': id});
    if (res['error'] != null) {
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return false;
    }
    _startModelStatusPolling();
    unawaited(_pollModelStatus());
    return true;
  }

  Future<void> cancelModelInstall(String id) async {
    if (_client == null) return;
    await _client!.call('models.cancel', {'modelId': id});
    await _pollModelStatus();
  }

  /// Unload the model from the AI worker and stop the worker process.
  /// The installed artifact stays put — this only frees the runtime.
  Future<void> unloadModel() async {
    await ai.shutdownForRemoval();
    notifyListeners();
  }

  /// Unload the AI worker first, then remove the installed artifact. The
  /// engine still refuses removal while the file is held open.
  Future<bool> removeModel(String id) async {
    if (_client == null) return false;
    await ai.shutdownForRemoval();
    final res = await _client!.call('models.remove', {'modelId': id});
    if (res['error'] != null) {
      errorMessage = res['error']['message'] as String?;
      notifyListeners();
      return false;
    }
    await _pollModelStatus();
    return true;
  }

  void _startModelStatusPolling() {
    _modelPollTimer ??=
        Timer.periodic(const Duration(milliseconds: 400), (_) {
      unawaited(_pollModelStatus());
    });
  }

  Future<void> _pollModelStatus() async {
    if (_client == null) return;
    final res = await _client!.call('models.status');
    if (res['error'] != null) return;
    _mergeModelStates(res['result'] as Map<String, dynamic>?);
    // Stop polling once no lifecycle is in flight.
    if (!aiModels.any((m) => m.status?.isActive ?? false)) {
      _modelPollTimer?.cancel();
      _modelPollTimer = null;
    }
  }

  /// Optional local-AI worker — lazily spawned, separate process and pipe.
  /// Its absence or failure never affects the deterministic engine.
  final AiWorker ai = AiWorker();

  /// The one bounded AI task: interpret a natural-language goal into a
  /// validated intent for the deterministic planner. [recommendations] must
  /// already be the bounded summary form (id/title/category/bytes/confidence).
  Future<Map<String, dynamic>?> interpretGoal(
          String goal, List<Map<String, dynamic>> recommendations) =>
      ai.interpretGoal(goal, recommendations);

  @override
  void dispose() {
    _pollTimer?.cancel();
    _smartPollTimer?.cancel();
    _staleRefreshTimer?.cancel();
    _modelPollTimer?.cancel();
    _client?.dispose();
    _engine?.kill();
    ai.dispose();
    super.dispose();
  }
}
