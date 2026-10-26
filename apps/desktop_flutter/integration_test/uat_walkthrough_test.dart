// ignore_for_file: avoid_print
import 'dart:convert';
import 'dart:io';

import 'package:clearforge_desktop/app/clearforge_app.dart';
import 'package:clearforge_desktop/core/state/engine_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

/// UAT walkthrough: drives every screen of the real app against the real
/// engine, captures screenshots into docs/screenshots/, and prints a
/// metrics JSON block (read-only surfaces only — no mutation is executed).
Future<bool> waitFor(
    WidgetTester tester, bool Function() cond, Duration timeout) async {
  final end = DateTime.now().add(timeout);
  while (DateTime.now().isBefore(end)) {
    await tester.pump(const Duration(milliseconds: 300));
    if (cond()) return true;
  }
  return cond();
}

Directory shotsDir() {
  var d = Directory.current;
  for (var i = 0; i < 8 && !Directory('${d.path}\\docs').existsSync(); i++) {
    d = d.parent;
  }
  final out = Directory('${d.path}\\docs\\screenshots');
  out.createSync(recursive: true);
  return out;
}

Future<void> shot(IntegrationTestWidgetsFlutterBinding binding,
    String name, Directory dir) async {
  try {
    final bytes = await binding.takeScreenshot(name);
    File('${dir.path}\\$name.png').writeAsBytesSync(bytes);
    print('[UAT] shot $name.png (${bytes.length}B)');
  } catch (e) {
    print('[UAT] shot $name FAILED: $e');
  }
}

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  final metrics = <String, dynamic>{};

  testWidgets('UAT walkthrough: all screens, real engine, screenshots',
      (tester) async {
    final pipe = 'cf-uat-${DateTime.now().microsecondsSinceEpoch.toRadixString(16)}';
    final controller = EngineController(pipeName: pipe);
    await tester.pumpWidget(ClearForgeApp(controller: controller));
    await tester.pump(const Duration(seconds: 2));
    expect(await waitFor(tester,
        () => controller.status == EngineStatus.ready,
        const Duration(seconds: 60)), isTrue,
        reason: 'engine not ready: ${controller.errorMessage}');
    metrics['engineVersion'] = controller.engineVersion;
    final dir = shotsDir();

    // --- Smart Scan: real run, captured in progress (live progress +
    // findings streaming in). A full-machine scan legitimately takes
    // several minutes (duplicate hashing); the UAT records the running
    // snapshot, not a fabricated completion.
    await controller.startSmartScan();
    await waitFor(
        tester,
        () => (controller.smartScan?.findings.length ?? 0) > 0,
        const Duration(seconds: 240));
    await tester.pump(const Duration(seconds: 2));
    final ss = controller.smartScan;
    metrics['smartScan'] = {
      'status': ss?.status,
      'findings': ss?.findings.length ?? 0,
      'categories':
          ss?.findings.map((f) => f.category).toSet().toList(),
      'candidateBytes': ss?.findings.fold<int>(
          0, (a, f) => a + f.estimatedLogicalBytes),
      'note': 'captured mid-run — full scan exceeds UAT window',
    };
    print('[UAT] smart scan: ${ss?.status}, '
        '${ss?.findings.length ?? 0} findings at capture');
    await shot(binding, '01-smart-scan', dir);

    // --- Storage ---
    await controller.loadStorageIntelligence();
    await tester.pump(const Duration(seconds: 1));
    metrics['storage'] = {
      'totalLogicalBytes':
          controller.storageInventory?['totalLogicalBytes'],
      'partial': controller.storageInventory?['partial'],
      'recommendations': controller.recommendations.length,
      'executable': controller.recommendations
          .where((r) => r.actionRef != null).length,
    };
    await tester.tap(find.text('Storage'));
    await tester.pump(const Duration(seconds: 1));
    await shot(binding, '02-storage', dir);

    // --- Duplicates (screen; scan start deferred to a small-scope run) ---
    await tester.tap(find.text('Duplicates'));
    await tester.pump(const Duration(seconds: 1));
    await shot(binding, '03-duplicates', dir);

    // --- Startup ---
    await controller.loadStartup();
    metrics['startupEntries'] = controller.startupEntries.length;
    await tester.tap(find.text('Startup'));
    await tester.pump(const Duration(seconds: 1));
    await shot(binding, '04-startup', dir);

    // --- Apps ---
    await controller.loadApps();
    metrics['apps'] = {
      'count': controller.apps.length,
      'withUninstaller':
          controller.apps.where((a) => a['hasUninstaller'] == true).length,
      'systemComponents':
          controller.apps.where((a) => a['kind'] != 'app').length,
    };
    await tester.tap(find.text('Apps'));
    await tester.pump(const Duration(seconds: 1));
    await shot(binding, '05-apps', dir);

    // --- Privacy ---
    await controller.loadPrivacy();
    metrics['privacy'] = controller.privacyProfiles.length;
    await tester.tap(find.text('Privacy'));
    await tester.pump(const Duration(seconds: 1));
    await shot(binding, '06-privacy', dir);

    // --- AI Models ---
    await controller.loadModelCatalog();
    metrics['modelCatalogEntries'] = controller.aiModels.length;
    await tester.tap(find.text('AI Models'));
    await tester.pump(const Duration(seconds: 1));
    await shot(binding, '07-models', dir);

    // --- History (real journal) ---
    await controller.refreshHistory();
    metrics['historyEntries'] = controller.history.length;
    await tester.tap(find.text('History'));
    await tester.pump(const Duration(seconds: 1));
    await shot(binding, '08-history', dir);

    print('[UAT] METRICS ${jsonEncode(metrics)}');
    expect(controller.status, EngineStatus.ready);
  }, timeout: const Timeout(Duration(minutes: 14)));
}
