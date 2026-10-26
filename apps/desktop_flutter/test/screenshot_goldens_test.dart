import 'package:clearforge_desktop/core/models/scan_models.dart';
import 'package:clearforge_desktop/core/state/engine_controller.dart';
import 'package:clearforge_desktop/features/apps/apps_screen.dart';
import 'package:clearforge_desktop/features/history/history_screen.dart';
import 'package:clearforge_desktop/features/smartscan/smart_scan_screen.dart';
import 'package:clearforge_desktop/features/storage/storage_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// README screenshots: real widget rendering at desktop size, seeded with
// shapes captured from the live engine (apps=296, storage=141GB logical,
// 18 recs, M44 journal entries). Update goldens via --update-goldens.
Widget wrap(Widget child) => MaterialApp(
    theme: ThemeData(colorSchemeSeed: Colors.teal, useMaterial3: true),
    home: Scaffold(body: child));

SmartFinding f(String id, String cat, String title, int bytes,
        {bool actionable = true, String risk = 'low'}) =>
    SmartFinding(
        id: id, producer: 'catalog', category: cat, title: title,
        reason: 'matched versioned cleanup catalog', evidence: const [],
        confidence: 'high', risk: risk == 'low' ? RiskLevel.low : RiskLevel.medium,
        estimatedLogicalBytes: bytes, reversibility: 'quarantinable',
        actionable: actionable);

void main() {
  testWidgets('screenshots', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 800));
    tester.view.physicalSize = const Size(1280, 800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final c = EngineController()
      ..status = EngineStatus.ready
      ..engineVersion = '0.1.0-m1';

    c.smartScan = SmartScanSnapshot(
        smartScanId: 's1', status: 'completed', deduplicated: 2,
        estimatedLogicalBytes: 12600000000,
        producers: const [],
        findings: [
          f('a', 'package-manager-caches', 'Package manager caches',
              11631918100, risk: 'medium'),
          f('b', 'temporary-files', 'Temporary files (>24h)', 842000000),
          f('c', 'browser-cache', 'Browser cache (closed profiles)',
              316001422),
          f('d', 'crash-dumps', 'Crash dumps', 52428800),
        ]);
    await tester.pumpWidget(wrap(SmartScanScreen(controller: c)));
    await tester.pump(const Duration(milliseconds: 300));
    await expectLater(find.byType(SmartScanScreen),
        matchesGoldenFile('goldens/01-smart-scan.png'));

    c.storageInventory = {
      'totalLogicalBytes': 151340098803, 'partial': false,
      'scopes': {'downloads': []},
    };
    await tester.pumpWidget(wrap(StorageScreen(controller: c)));
    await tester.pump(const Duration(milliseconds: 300));
    await expectLater(find.byType(StorageScreen),
        matchesGoldenFile('goldens/02-storage.png'));

    c.apps = [
      for (final e in [
        ('Visual Studio Code', 'Microsoft Corporation', '1.95', 412000000),
        ('Steam', 'Valve Corporation', '2.10', 2100000000),
        ('Git', 'The Git Development Community', '2.45', 385000000),
        ('Node.js', 'Node.js Foundation', '22.1', 104000000),
      ])
        {
          'name': e.$1, 'publisher': e.$2, 'version': e.$3,
          'installLocation': 'C:\\Program Files\\${e.$1}',
          'sizeBytes': e.$4, 'installDate': '2024-06-01',
          'source': 'registry-hklm-64', 'scope': 'machine', 'kind': 'app',
          'hasUninstaller': true, 'confidence': 'registered',
        }
    ];
    c.appsNote = 'Read-only — uninstall via Settings > Apps';
    await tester.pumpWidget(wrap(AppsScreen(controller: c)));
    await tester.pump(const Duration(milliseconds: 300));
    await expectLater(
        find.byType(AppsScreen), matchesGoldenFile('goldens/05-apps.png'));

    c.history = [
      JournalEntry('op1', 's1', 's1:0', 'temporary-files', 4096,
          'quarantined', null, 'cf_m44_alpha.tmp', '2026-10-26T16:20Z',
          'verified'),
      JournalEntry('op2', 's2', 's2:0', 'temporary-files', 4096,
          'restored', null, 'cf_m44_alpha.tmp', '2026-10-26T16:21Z',
          'restored'),
    ];
    await tester.pumpWidget(wrap(HistoryScreen(controller: c)));
    await tester.pump(const Duration(milliseconds: 300));
    await expectLater(find.byType(HistoryScreen),
        matchesGoldenFile('goldens/08-history.png'));
  });
}
