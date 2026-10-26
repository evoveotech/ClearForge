import 'package:flutter_test/flutter_test.dart';
import 'package:clearforge_desktop/core/models/scan_models.dart';
import 'package:clearforge_desktop/core/state/engine_controller.dart';

// M8.3 duplicate review client-side coverage: groupId decoding, the rule
// that keeper-review findings can never join bulk selection, and honest
// handling when the engine has no group detail to show.

Map<String, dynamic> findingJson({
  String id = 'dup-0',
  String? groupId,
  String? sessionState,
  List<String>? candidateIds = const ['s1:d0', 's1:d1', 's1:d2'],
}) =>
    {
      'id': id,
      'producer': 'duplicates',
      'category': 'duplicates',
      'title': 'Duplicate group g0',
      'reason': '3 byte-identical files',
      'evidence': ['member: C:\\a\\x.bin'],
      'confidence': 'high',
      'risk': 'medium',
      'estimatedLogicalBytes': 4096,
      'reversibility': 'quarantinable',
      'actionable': true,
      'skipReason': null,
      'sessionId': 's1',
      'candidateIds': candidateIds,
      'duplicateOf': null,
      'groupId': groupId,
      'sessionState': sessionState,
    };

void main() {
  group('M8.3 finding decode', () {
    test('groupId decodes and marks keeper-review findings', () {
      final f = SmartFinding.fromJson(findingJson(groupId: 'g0'));
      expect(f.groupId, 'g0');
      expect(f.requiresKeeperReview, isTrue);
      // All members stay bound — keeper choice happens at review.
      expect(f.candidateIds, hasLength(3));
    });

    test('findings without groupId are unchanged', () {
      final f = SmartFinding.fromJson(findingJson());
      expect(f.groupId, isNull);
      expect(f.requiresKeeperReview, isFalse);
      expect(f.selectable, isTrue);
    });
  });

  group('M8.3 selection gating', () {
    test('keeper-review findings are never bulk-selectable', () {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      c.smartScan = SmartScanSnapshot.fromJson({
        'smartScanId': 'ss1',
        'status': 'completed',
        'userCancelled': false,
        'producers': [],
        'findings': [findingJson(groupId: 'g0', sessionState: 'live')],
        'deduplicated': 0,
        'estimatedLogicalBytes': 4096,
      });
      c.toggleSmartSelected('dup-0', true);
      expect(c.smartSelected, isEmpty);
      c.dispose();
    });

    test('stale keeper-review finding stays review-only', () {
      final f = SmartFinding.fromJson(
          findingJson(groupId: 'g0', sessionState: 'expired'));
      expect(f.requiresKeeperReview, isTrue);
      expect(f.stale, isTrue);
    });
  });

  group('M8.3 controller surface', () {
    test('group detail returns null with no engine', () async {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      expect(await c.duplicateGroupDetail('s1', 'g0'), isNull);
      c.dispose();
    });

    test('empty selection never issues a commit', () async {
      final c = EngineController(pipeName: 'cf-test-never-connect');
      expect(await c.commitDuplicateGroup('s1', 'g0', 'k', const []),
          'invalid_params');
      expect(c.lastCommitResults, isNull);
      c.dispose();
    });
  });
}
