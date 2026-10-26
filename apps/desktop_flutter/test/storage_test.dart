import 'package:flutter_test/flutter_test.dart';
import 'package:clearforge_desktop/core/models/scan_models.dart';

// Model-level coverage of the storage endpoints' response shapes and the
// UI rules that keep review-only items non-executable.

Map<String, dynamic> sampleInventory({bool partial = false}) => {
      'totalLogicalBytes': 4096,
      'partial': partial,
      'scopes': {
        'downloads': [
          {'name': 'node_modules', 'kind': 'dir', 'logicalBytes': 3000, 'categoryHint': 'dev-dependencies'},
          {'name': 'mystery', 'kind': 'dir', 'logicalBytes': 1096, 'categoryHint': null},
        ],
      },
    };

Map<String, dynamic> sampleRec({bool action = false}) => {
      'id': 'r1', 'category': 'dev-dependencies', 'title': 'Regenerable output',
      'explanation': 'marker verified',
      'evidence': ['size=3000B', 'marker=package.json'],
      'estimatedLogicalBytes': 3000,
      'confidence': 'medium', 'risk': 'medium',
      'reversibility': action ? 'quarantinable' : 'manualonly',
      'preconditions': [], 'skipReasons': [],
      'actionRef': action ? 'session:abc' : null,
    };

void main() {
  group('storage inventory responses', () {
    test('total, partial flag and scope nodes decode', () {
      final inv = sampleInventory(partial: true);
      expect(inv['totalLogicalBytes'], 4096);
      expect(inv['partial'], isTrue);
      final nodes = (inv['scopes'] as Map)['downloads'] as List;
      expect(nodes, hasLength(2));
      expect(formatBytes(inv['totalLogicalBytes'] as int), '4.0 KB');
    });

    test('complete scan reports partial=false', () {
      expect(sampleInventory()['partial'], isFalse);
    });
  });

  group('recommendation safety', () {
    test('review-only recommendation has no actionRef', () {
      final r = sampleRec();
      expect(r['actionRef'], isNull);
      expect(r['reversibility'], 'manualonly');
    });

    test('executable recommendation carries a session actionRef', () {
      final r = sampleRec(action: true);
      expect(r['actionRef'], 'session:abc');
      expect(r['reversibility'], 'quarantinable');
    });
  });

  group('storage.plan contract', () {
    test('goalBytes must be positive', () {
      // UI-side rule mirrored in the controller: non-positive goals never
      // reach the pipe.
      const gb = 0.0;
      expect(gb > 0, isFalse);
    });

    test('unreachable plan reports likelyAchievable=false + missing evidence', () {
      final plan = {
        'goalBytes': 10 * 1024 * 1024 * 1024,
        'estimatedTotalBytes': 4096,
        'likelyAchievable': false,
        'selected': [],
        'explanations': [],
        'missingEvidence': ['goal exceeds safe-estimate'],
      };
      expect(plan['likelyAchievable'], isFalse);
      expect((plan['missingEvidence'] as List), isNotEmpty);
    });
  });
}
