import 'package:flutter_test/flutter_test.dart';
import 'package:clearforge_desktop/core/models/scan_models.dart';
import 'package:clearforge_desktop/core/state/engine_controller.dart';

// M8.1 Smart Scan client-side coverage: response decoding, the rule that
// only actionable findings can ever be selected, and honest status handling.

Map<String, dynamic> findingJson({
  String id = 'f-0',
  String producer = 'catalog',
  String category = 'temporary-files',
  bool actionable = true,
  String risk = 'low',
  int bytes = 2048,
  String? sessionId = 'sess1',
  List<String>? candidateIds = const ['sess1:0'],
  String? skipReason,
  String? sessionState,
  String? duplicateOf,
}) =>
    {
      'id': id,
      'producer': producer,
      'category': category,
      'title': 't',
      'reason': 'r',
      'evidence': ['files=3'],
      'confidence': 'high',
      'risk': risk,
      'estimatedLogicalBytes': bytes,
      'reversibility': 'quarantinable',
      'actionable': actionable,
      'skipReason': skipReason,
      'sessionId': sessionId,
      'candidateIds': candidateIds,
      'duplicateOf': duplicateOf,
      'sessionState': sessionState,
    };

Map<String, dynamic> snapshotJson(
        {String status = 'completed', bool userCancelled = false}) =>
    {
      'smartScanId': 'ss1',
      'status': status,
      'userCancelled': userCancelled,
      'producers': [
        {'producerId': 'catalog', 'status': 'completed', 'findings': 2},
        {'producerId': 'startup', 'status': 'failed', 'findings': 0,
         'diagnostic': 'InvalidOperationException'},
      ],
      'findings': [
        findingJson(id: 'f-0', actionable: true),
        findingJson(
            id: 'f-1', actionable: false, sessionId: null,
            candidateIds: null, skipReason: 'no supported action',
            category: 'startup-items', bytes: 0),
      ],
      'deduplicated': 1,
      'estimatedLogicalBytes': 2048,
    };

void main() {
  group('smart scan model decoding', () {
    test('snapshot decodes status, producers and findings', () {
      final s = SmartScanSnapshot.fromJson(snapshotJson());
      expect(s.smartScanId, 'ss1');
      expect(s.status, 'completed');
      expect(s.isTerminal, isTrue);
      expect(s.producers, hasLength(2));
      expect(s.producers[1].status, 'failed');
      expect(s.producers[1].diagnostic, 'InvalidOperationException');
      expect(s.findings, hasLength(2));
      expect(s.deduplicated, 1);
      expect(s.estimatedLogicalBytes, 2048);
    });

    test('finding decodes binding fields when actionable', () {
      final f = SmartFinding.fromJson(findingJson());
      expect(f.actionable, isTrue);
      expect(f.sessionId, 'sess1');
      expect(f.candidateIds, ['sess1:0']);
      expect(f.risk, RiskLevel.low);
    });

    test('review-only finding carries skip reason and no binding', () {
      final f = SmartFinding.fromJson(findingJson(
          actionable: false,
          sessionId: null,
          candidateIds: null,
          skipReason: 'no supported action'));
      expect(f.actionable, isFalse);
      expect(f.sessionId, isNull);
      expect(f.candidateIds, isNull);
      expect(f.skipReason, 'no supported action');
    });

    test('running snapshot is not terminal', () {
      final s = SmartScanSnapshot.fromJson(snapshotJson(status: 'running'));
      expect(s.isRunning, isTrue);
      expect(s.isTerminal, isFalse);
    });

    test('partial and cancelled statuses are distinguished', () {
      expect(SmartScanSnapshot.fromJson(snapshotJson(status: 'partial')).status,
          'partial');
      expect(
          SmartScanSnapshot.fromJson(snapshotJson(status: 'cancelled')).status,
          'cancelled');
      expect(SmartScanSnapshot.fromJson(snapshotJson(status: 'failed')).status,
          'failed');
    });
  });

  group('selection gating', () {
    test('review-only findings can never be selected', () {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      c.smartScan = SmartScanSnapshot.fromJson(snapshotJson());
      c.toggleSmartSelected('f-1', true); // review-only
      expect(c.smartSelected, isEmpty);
      c.toggleSmartSelected('f-0', true); // actionable
      expect(c.smartSelected, {'f-0'});
      c.dispose();
    });

    test('unknown finding ids are ignored', () {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      c.smartScan = SmartScanSnapshot.fromJson(snapshotJson());
      c.toggleSmartSelected('ghost', true);
      expect(c.smartSelected, isEmpty);
      c.dispose();
    });

    test('deselection removes the id', () {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      c.smartScan = SmartScanSnapshot.fromJson(snapshotJson());
      c.toggleSmartSelected('f-0', true);
      c.toggleSmartSelected('f-0', false);
      expect(c.smartSelected, isEmpty);
      c.dispose();
    });
  });

  group('session-state gating (M8.1.1)', () {
    test('sessionState decodes and stale findings are not selectable', () {
      final live = SmartFinding.fromJson(
          findingJson(id: 'live', sessionState: 'live'));
      final expired = SmartFinding.fromJson(
          findingJson(id: 'exp', sessionState: 'expired'));
      final missing = SmartFinding.fromJson(
          findingJson(id: 'miss', sessionState: 'missing'));
      final cancelled = SmartFinding.fromJson(
          findingJson(id: 'can', sessionState: 'cancelled'));
      expect(live.selectable, isTrue);
      expect(live.stale, isFalse);
      for (final f in [expired, missing, cancelled]) {
        expect(f.selectable, isFalse);
        expect(f.stale, isTrue);
      }
      // Absent field (older engine) behaves as live for an actionable
      // finding — back-compat; the engine still gates at commit.
      expect(SmartFinding.fromJson(findingJson()).selectable, isTrue);
    });

    test('expired finding cannot be toggled selected', () {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      final j = snapshotJson();
      (j['findings'] as List).add(
          findingJson(id: 'dead', sessionState: 'expired'));
      c.smartScan = SmartScanSnapshot.fromJson(j);
      c.toggleSmartSelected('dead', true);
      expect(c.smartSelected, isEmpty);
      expect(c.smartNeedsRescan, isTrue);
      c.dispose();
    });

    test('stale session ids proven at commit block re-selection', () {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      c.smartScan = SmartScanSnapshot.fromJson(snapshotJson());
      c.smartStaleSessions.add('sess1');
      c.toggleSmartSelected('f-0', true);
      expect(c.smartSelected, isEmpty);
      expect(c.smartNeedsRescan, isTrue);
      c.dispose();
    });

    test('committed findings are no longer selectable', () {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      c.smartScan = SmartScanSnapshot.fromJson(snapshotJson());
      c.smartCommitted.add('f-0');
      c.toggleSmartSelected('f-0', true);
      expect(c.smartSelected, isEmpty);
      c.dispose();
    });
  });

  group('cancelled-run handling (M8.1.1)', () {
    test('userCancelled decodes from the snapshot', () {
      expect(
          SmartScanSnapshot.fromJson(snapshotJson(userCancelled: true))
              .userCancelled,
          isTrue);
      expect(SmartScanSnapshot.fromJson(snapshotJson()).userCancelled,
          isFalse);
    });
  });

  group('outcome classification', () {
    test('integrity errors are counted separately from skips', () {
      final items = [
        const CleanupItemOutcome('c1', 'cleaned', 100, null),
        const CleanupItemOutcome('c2', 'error', 0, 'integrity_mismatch'),
        const CleanupItemOutcome('c3', 'skipped', 0, 'skipped_locked'),
      ];
      final integrity = items
          .where((i) => (i.errorCode ?? '').startsWith('integrity_'))
          .length;
      final cleaned = items.where((i) => i.status == 'cleaned').length;
      expect(cleaned, 1);
      expect(integrity, 1);
      expect(items.length - cleaned - integrity, 1);
    });
  });
}
