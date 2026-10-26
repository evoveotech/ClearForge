import 'package:clearforge_desktop/core/state/plan_guard.dart';
import 'package:flutter_test/flutter_test.dart';

Map<String, dynamic> action(String id, String session) => {
      'id': id,
      'action': {'sessionId': session, 'status': 'completed'},
    };

void main() {
  group('approvedSetIntact', () {
    test('identical subset is intact', () {
      final approved = [action('a', 's1'), action('b', 's2')];
      final replanned = [action('a', 's1'), action('b', 's2'), action('c', 's3')];
      expect(approvedSetIntact(approved, replanned), isTrue);
    });

    test('rebound session is a change', () {
      final approved = [action('a', 's1')];
      final replanned = [action('a', 's9')];
      expect(approvedSetIntact(approved, replanned), isFalse);
    });

    test('missing approved action is a change', () {
      final approved = [action('a', 's1'), action('b', 's2')];
      final replanned = [action('a', 's1')];
      expect(approvedSetIntact(approved, replanned), isFalse);
    });

    test('empty approved set is never intact', () {
      expect(approvedSetIntact(const [], [action('a', 's1')]), isFalse);
    });

    test('review-only item without action still compares by id', () {
      const noAction = {'id': 'rev-1'};
      expect(approvedSetIntact([noAction], [noAction]), isTrue);
    });
  });

  group('requiresBatchConfirm', () {
    test('small batch needs no confirm', () {
      expect(requiresBatchConfirm(3, 1 << 20), isFalse);
    });
    test('candidate count over threshold confirms', () {
      expect(requiresBatchConfirm(kBatchConfirmCandidates + 1, 0), isTrue);
    });
    test('bytes over threshold confirms', () {
      expect(requiresBatchConfirm(5, kBatchConfirmBytes + 1), isTrue);
    });
    test('boundary values do not confirm', () {
      expect(requiresBatchConfirm(kBatchConfirmCandidates, kBatchConfirmBytes),
          isFalse);
    });
  });
}
