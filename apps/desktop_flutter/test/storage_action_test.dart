import 'package:clearforge_desktop/core/models/scan_models.dart';
import 'package:clearforge_desktop/core/state/engine_controller.dart';
import 'package:clearforge_desktop/features/storage/storage_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// M4.2: executable vs review-only recommendation handling. The `executable`
// gate is the single UI-side decision; the engine re-validates everything.

Map<String, dynamic> recJson({
  String category = 'temporary-files',
  String reversibility = 'quarantinable',
  String? actionRef,
  Map<String, dynamic>? action,
}) =>
    {
      'id': 'r-$category',
      'category': category,
      'title': 'Clean $category',
      'explanation': 'rule-matched candidates',
      'evidence': ['candidates=2', 'eligible=100B'],
      'estimatedLogicalBytes': 100,
      'confidence': 'high',
      'risk': 'low',
      'reversibility': reversibility,
      'preconditions': [],
      'skipReasons': [],
      'actionRef': actionRef,
      'action': action,
    };

Map<String, dynamic> actionJson({
  String kind = 'quarantine',
  String sessionId = 's1',
  String status = 'completed',
  int candidateCount = 2,
  int eligibleBytes = 100,
}) =>
    {
      'kind': kind,
      'sessionId': sessionId,
      'status': status,
      'candidateCount': candidateCount,
      'eligibleBytes': eligibleBytes,
    };

void main() {
  group('StorageRecommendation.fromJson', () {
    test('parses a bound action', () {
      final r = StorageRecommendation.fromJson(recJson(
          actionRef: 'session:s1', action: actionJson()));
      expect(r.action, isNotNull);
      expect(r.action!.sessionId, 's1');
      expect(r.action!.candidateCount, 2);
      expect(r.actionRef, 'session:s1');
    });

    test('review-only rec decodes with null action', () {
      final r = StorageRecommendation.fromJson(
          recJson(reversibility: 'manualonly'));
      expect(r.action, isNull);
      expect(r.actionRef, isNull);
      expect(r.executable, isFalse);
    });
  });

  group('executable gate', () {
    test('requires a completed quarantine session binding', () {
      expect(
          StorageRecommendation.fromJson(recJson(
                  actionRef: 'session:s1', action: actionJson()))
              .executable,
          isTrue);
    });

    test('rejects missing action', () {
      expect(
          StorageRecommendation.fromJson(recJson(actionRef: 'session:s1'))
              .executable,
          isFalse);
    });

    test('rejects non-quarantine kinds', () {
      expect(
          StorageRecommendation.fromJson(recJson(
                  actionRef: 'session:s1',
                  action: actionJson(kind: 'delete')))
              .executable,
          isFalse);
    });

    test('rejects non-completed sessions', () {
      for (final st in ['scanning', 'cancelled', 'failed']) {
        expect(
            StorageRecommendation.fromJson(recJson(
                    actionRef: 'session:s1',
                    action: actionJson(status: st)))
                .executable,
            isFalse,
            reason: 'status=$st must not be executable');
      }
    });

    test('rejects empty candidate sets and forged refs', () {
      expect(
          StorageRecommendation.fromJson(recJson(
                  actionRef: 'session:s1',
                  action: actionJson(candidateCount: 0)))
              .executable,
          isFalse);
      // actionRef not matching the bound session is treated as forged/stale.
      expect(
          StorageRecommendation.fromJson(recJson(
                  actionRef: 'session:other',
                  action: actionJson(sessionId: 's1')))
              .executable,
          isFalse);
      expect(
          StorageRecommendation.fromJson(recJson(
                  actionRef: 'session:s1',
                  action: actionJson(sessionId: '')))
              .executable,
          isFalse);
    });
  });

  group('storage screen action gating', () {
    EngineController controllerWith(List<StorageRecommendation> recs) {
      final c = EngineController();
      c.storageInventory = {
        'totalLogicalBytes': 2048,
        'partial': false,
        'scopes': {
          's': [
            {'name': 'd', 'kind': 'dir', 'logicalBytes': 2048, 'categoryHint': null}
          ]
        },
      };
      c.recommendations = recs;
      return c;
    }

    testWidgets('executable rec offers review & quarantine; review-only does not',
        (tester) async {
      final c = controllerWith([
        StorageRecommendation.fromJson(recJson(
            actionRef: 'session:s1', action: actionJson())),
        StorageRecommendation.fromJson(recJson(
            category: 'unclassified', reversibility: 'manualonly')),
      ]);
      await tester.pumpWidget(
          MaterialApp(home: Scaffold(body: StorageScreen(controller: c))));

      // Expand both tiles so the gated affordance is built.
      await tester.tap(find.text('Clean temporary-files'));
      await tester.tap(find.text('Clean unclassified'));
      await tester.pumpAndSettle();

      expect(find.textContaining('Review & quarantine'), findsOneWidget);
      expect(find.textContaining('No automated action'), findsOneWidget);
    });

    testWidgets('stale (non-completed) action is not executable',
        (tester) async {
      final c = controllerWith([
        StorageRecommendation.fromJson(recJson(
            actionRef: 'session:s1',
            action: actionJson(status: 'cancelled'))),
      ]);
      await tester.pumpWidget(
          MaterialApp(home: Scaffold(body: StorageScreen(controller: c))));
      await tester.tap(find.text('Clean temporary-files'));
      await tester.pumpAndSettle();
      expect(find.textContaining('Review & quarantine'), findsNothing);
      expect(find.textContaining('review-only'), findsWidgets);
    });
  });
}
