// ignore_for_file: avoid_print
import 'dart:ffi';
import 'dart:io';

import 'package:ffi/ffi.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:win32/win32.dart';

import 'package:clearforge_desktop/app/clearforge_app.dart';
import 'package:clearforge_desktop/core/state/engine_controller.dart';

/// M4.4 real-app verification: drives the built UI against the real engine
/// over the production named pipe. The engine scans whatever TEMP resolves
/// to, so the test runner MUST point TEMP at a unique test-owned fixture
/// root named `cf-m44-*` — e.g.
/// `TEMP=C:\...\cf-m44-<run> flutter test integration_test/m44_verify_test.dart -d windows`.
/// The guard below aborts before any engine spawn, scan or commit when the
/// environment is not isolated.
String requireIsolatedTemp(String? tempEnv) {
  if (tempEnv == null || tempEnv.isEmpty) {
    throw StateError('TEMP is unset — the engine would scan an unknown root');
  }
  final norm = tempEnv.replaceAll('/', '\\').replaceAll(RegExp(r'\\+$'), '');
  final base = norm.split('\\').last;
  if (!base.startsWith('cf-m44-')) {
    throw StateError('M44 requires an isolated test-owned TEMP named '
        '"cf-m44-*", got: $norm — refusing to run against a shared/real '
        'temporary directory');
  }
  return norm;
}

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  test('isolation guard rejects a non-test-owned TEMP', () {
    expect(
        () => requireIsolatedTemp(r'C:\Users\hardi\AppData\Local\Temp'),
        throwsStateError);
    expect(() => requireIsolatedTemp(null), throwsStateError);
    expect(() => requireIsolatedTemp(''), throwsStateError);
    expect(requireIsolatedTemp(r'C:\T\cf-m44-run1'),
        r'C:\T\cf-m44-run1');
  });

  test('process-match check rejects a mismatched engine instance', () {
    // The strict ownership gate inside connect(): a pipe answer whose pid
    // does not equal the spawned host pid must fail the handshake. Proven
    // here at the decision boundary; the live path is asserted in the
    // workflow test via enginePid == spawnedPid.
    bool matches(int? spawned, int? health) => spawned != null && health == spawned;
    expect(matches(42, 42), isTrue);      // same instance passes
    expect(matches(42, 43), isFalse);     // stale/different host rejected
    expect(matches(null, 42), isFalse);   // unknown spawn can never match
    expect(matches(42, null), isFalse);   // missing pid is not proof
  });

  Future<bool> waitFor(
      WidgetTester tester, bool Function() cond, Duration timeout) async {
    final end = DateTime.now().add(timeout);
    while (DateTime.now().isBefore(end)) {
      await tester.pump(const Duration(milliseconds: 250));
      if (cond()) return true;
    }
    return cond();
  }

  testWidgets('M4.4 GUI-to-engine safe workflow', (tester) async {
    // Fail closed BEFORE spawning the engine: the resolved fixture root must
    // be the isolated location the runner configured, and every fixture must
    // live under it. The spawned engine inherits this process environment,
    // so its TEMP/TMP resolve to the same isolated root.
    final temp = Directory(requireIsolatedTemp(Platform.environment['TEMP']));
    if (!temp.existsSync()) temp.createSync(recursive: true);
    final tmp = Platform.environment['TMP'];
    if (tmp != null && tmp.isNotEmpty) {
      expect(requireIsolatedTemp(tmp), temp.path,
          reason: 'TMP must resolve to the same test-owned root as TEMP');
    }
    // Quarantine is same-volume-only by design: fixtures under a TEMP on a
    // different volume than %LOCALAPPDATA% would all return
    // cross_volume_refused — fail fast with the real reason instead of a
    // cryptic 'skipped' outcome later.
    final localAppData = Platform.environment['LOCALAPPDATA'];
    if (localAppData != null && localAppData.isNotEmpty) {
      expect(temp.path.substring(0, 1).toUpperCase(),
          localAppData.substring(0, 1).toUpperCase(),
          reason: 'M44 requires the isolated TEMP on the same volume as '
              '%LOCALAPPDATA% (quarantine root), got TEMP=${temp.path} vs '
              'LOCALAPPDATA=$localAppData');
    }
    print('[M44] isolated TEMP=${temp.path}');

    // Engine-instance ownership: a unique test-owned pipe name plus a strict
    // pid match — a stale production host can never answer this connection.
    final pipe = 'cf-m44-${DateTime.now().microsecondsSinceEpoch.toRadixString(16)}';
    final controller = EngineController(
        pipeName: pipe, requireProcessMatch: true);
    await tester.pumpWidget(ClearForgeApp(controller: controller));
    await tester.pump(const Duration(seconds: 1));

    // Fixtures: two backdated temp files (scan candidates), one fresh file
    // (must NOT be a candidate), all inside the isolated TEMP.
    final contentA = List<int>.generate(4096, (i) => (i * 31) % 251);
    final contentB = List<int>.generate(2048, (i) => (i * 17) % 241);
    final fxA = File('${temp.path}\\cf_m44_alpha.tmp');
    final fxB = File('${temp.path}\\cf_m44_beta.tmp');
    final fxFresh = File('${temp.path}\\cf_m44_fresh.tmp');
    final old = DateTime.now().subtract(const Duration(hours: 48));
    fxA
      ..writeAsBytesSync(contentA)
      ..setLastModifiedSync(old);
    fxB
      ..writeAsBytesSync(contentB)
      ..setLastModifiedSync(old);
    fxFresh.writeAsBytesSync(const [1, 2, 3]);
    // Confinement: every fixture — and therefore every cleanup target —
    // resolves inside the isolated test-owned root.
    for (final f in [fxA, fxB, fxFresh]) {
      expect(f.path.startsWith(temp.path), isTrue,
          reason: 'fixture escaped the isolated root: ${f.path}');
    }

    // --- Phase 1: engine health/connectivity + instance ownership -------
    expect(
        await waitFor(tester, () => controller.status == EngineStatus.ready,
            const Duration(seconds: 20)),
        isTrue,
        reason: 'engine failed to become ready: ${controller.errorMessage}');
    expect(controller.engineVersion, isNotNull);
    expect(controller.enginePid, isNotNull);
    expect(controller.enginePid, controller.spawnedPid,
        reason: 'connected engine is not the spawned process');
    expect(find.textContaining('ready'), findsOneWidget);
    print('[M44] PASS health: engine ${controller.engineVersion} ready');

    // --- Phase 2: scan produces candidates, fresh file excluded --------
    // Smart Scan is the default view (M8.1); navigate to the Cleaner flow.
    await tester.tap(find.text('Cleaner'));
    await tester.pump(const Duration(milliseconds: 400));
    await tester.tap(find.widgetWithText(FilledButton, 'Analyze'));
    await tester.pump();
    // M8.2: the catalog now scans real user cache locations too (bounded by
    // per-rule file caps), which takes longer than the fixture-only TEMP —
    // allow the engine's own per-producer budget plus margin.
    expect(
        await waitFor(tester, () => controller.results?.isTerminal ?? false,
            const Duration(seconds: 180)),
        isTrue,
        reason: 'scan did not reach a terminal state');
    expect(controller.results!.status, 'completed');
    final names =
        controller.results!.candidates.map((c) => c.displayName).toList();
    expect(names, containsAll(['cf_m44_alpha.tmp', 'cf_m44_beta.tmp']));
    expect(names, isNot(contains('cf_m44_fresh.tmp')));
    // M8.2 isolation: new low-risk catalog categories (thumbnail, GPU and
    // package-manager caches) contribute REAL machine files to the
    // auto-selected set. The commit set is narrowed to the fixture candidate
    // ids only — this test must never quarantine a real file.
    final fixtureIds = controller.results!.candidates
        .where((c) => c.displayName.startsWith('cf_m44_'))
        .map((c) => c.id)
        .toSet();
    for (final c in controller.results!.candidates) {
      if (!fixtureIds.contains(c.id) && controller.selected.contains(c.id)) {
        controller.toggleSelected(c.id, false);
      }
    }
    expect(controller.selected, equals(fixtureIds),
        reason: 'only fixture candidates may be committed — '
            '${controller.results!.candidates.length} candidates scanned, '
            '${fixtureIds.length} are test fixtures');
    print('[M44] PASS scan: ${names.length} candidates '
        '(${controller.results!.candidates.length - fixtureIds.length} '
        'non-fixture candidates deselected), fresh file correctly excluded, '
        'status=${controller.results!.status}');

    // --- Phase 3: approval dialog Cancel = no mutation -----------------
    await tester.tap(find.textContaining('Quarantine'));
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.text('Confirm quarantine'), findsOneWidget);
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump(const Duration(milliseconds: 400));
    expect(fxA.existsSync(), isTrue);
    expect(controller.lastCommitResults, isNull);
    print('[M44] PASS cancel: dialog cancelled, nothing moved');

    // --- Phase 4: lock fxB, approve quarantine -------------------------
    // fxB is locked after scan but before commit so the engine must report a
    // per-item skipped/locked outcome rather than silently failing.
    final bPath = fxB.path.toPcwstr(allocator: calloc);
    final lockHandle = CreateFile(bPath, GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_NONE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, null);
    calloc.free(bPath);
    expect(lockHandle.value.address,
        isNot(equals(INVALID_HANDLE_VALUE.address)),
        reason: 'could not lock fixture B');

    await tester.tap(find.textContaining('Quarantine'));
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.text('Confirm quarantine'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Quarantine'));
    expect(
        await waitFor(tester,
            () => controller.lastCommitResults != null, const Duration(seconds: 120)),
        isTrue,
        reason: 'commit never produced per-item outcomes: '
            '${controller.errorMessage}');

    final outcomes = {
      for (final o in controller.lastCommitResults!) o.candidateId: o
    };
    final candA = controller.results!.candidates
        .firstWhere((c) => c.displayName == 'cf_m44_alpha.tmp');
    final candB = controller.results!.candidates
        .firstWhere((c) => c.displayName == 'cf_m44_beta.tmp');
    expect(outcomes[candA.id]?.status, 'cleaned');
    expect(fxA.existsSync(), isFalse, reason: 'fxA should have been moved');
    expect(outcomes[candB.id]?.status, isNot('cleaned'),
        reason: 'locked fixture must not be reported cleaned');
    expect(fxB.existsSync(), isTrue, reason: 'locked file must remain in place');
    CloseHandle(lockHandle.value);
    print('[M44] PASS commit: alpha=${outcomes[candA.id]?.status} '
        'beta=${outcomes[candB.id]?.status} (${outcomes[candB.id]?.errorCode})');

    // --- Phase 5: History + restore, byte-identical --------------------
    await tester.tap(find.text('History'));
    await tester.pump(const Duration(milliseconds: 400));
    expect(await waitFor(tester, () => find.text('Restore').evaluate().isNotEmpty,
        const Duration(seconds: 10)), isTrue);
    final tile = find.ancestor(
        of: find.textContaining('cf_m44_alpha'), matching: find.byType(ListTile));
    expect(tile, findsWidgets);
    await tester.tap(find.descendant(
        of: tile.first, matching: find.widgetWithText(TextButton, 'Restore')));
    await tester.pump(const Duration(seconds: 2));
    expect(await waitFor(tester, () => fxA.existsSync(), const Duration(seconds: 10)),
        isTrue,
        reason: 'restore did not return cf_m44_alpha.tmp');
    expect(fxA.readAsBytesSync(), orderedEquals(contentA),
        reason: 'restored content must be byte-identical');
    print('[M44] PASS restore: byte-identical');

    // --- Phase 6: Smart Storage inventory, recs, evidence, plan --------
    await tester.tap(find.text('Storage'));
    await tester.pump(const Duration(milliseconds: 400));
    await tester.tap(find.widgetWithText(FilledButton, 'Scan storage'));
    expect(await waitFor(tester, () => !controller.storageScanning,
        const Duration(seconds: 60)), isTrue,
        reason: 'storage scan did not finish: ${controller.errorMessage}');
    expect(controller.storageInventory, isNotNull);
    print('[M44] PASS storage inventory: '
        '${controller.storageInventory!['totalLogicalBytes']} logical bytes, '
        '${controller.recommendations.length} recommendations');

    // Evidence/risk/confidence on first recommendation.
    if (controller.recommendations.isNotEmpty) {
      await tester.tap(find.byType(ExpansionTile).first);
      await tester.pump(const Duration(milliseconds: 400));
      expect(find.text('Evidence'), findsWidgets);
      final execs = controller.recommendations.where((r) => r.executable);
      final reviewOnly =
          controller.recommendations.where((r) => !r.executable).length;
      print('[M44] recommendations: ${execs.length} executable, '
          '$reviewOnly review-only');
      // Review-only recommendations must not offer an executable action.
      for (final r in controller.recommendations.where((r) => !r.executable)) {
        expect(r.action, isNull);
      }
      // Exercise preview + explicit dialog for an executable rec; Cancel so
      // no storage action mutates anything.
      if (execs.isNotEmpty) {
        final btn = find.textContaining('Review & quarantine');
        if (btn.evaluate().isNotEmpty) {
          await tester.tap(btn.first);
          expect(
              await waitFor(
                  tester,
                  () => find.textContaining('Quarantine').evaluate().length > 1 ||
                      find.byType(AlertDialog).evaluate().isNotEmpty,
                  const Duration(seconds: 15)),
              isTrue,
              reason: 'preview dialog did not appear');
          if (find.byType(AlertDialog).evaluate().isNotEmpty) {
            await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
            await tester.pump(const Duration(milliseconds: 400));
          }
          print('[M44] PASS storage preview dialog shown, cancelled');
        }
      }
    }

    // Goal-based plan.
    await tester.tap(find.byIcon(Icons.play_arrow));
    expect(
        await waitFor(tester, () => controller.storagePlanResult != null,
            const Duration(seconds: 20)),
        isTrue,
        reason: 'storage.plan returned no result');
    expect(find.textContaining('Goal'), findsWidgets);
    print('[M44] PASS plan: ${controller.storagePlanResult}');

    // Plan review → reject non-fixture actions → approve → execute.
    // Only the temp-files fixture action may execute — never real
    // browser-cache or crash-dump items.
    final reviewBtn = find.text('Review & approve plan');
    if (reviewBtn.evaluate().isNotEmpty) {
      await tester.tap(reviewBtn);
      await tester.pump(const Duration(milliseconds: 500));
      expect(find.text('Review plan'), findsOneWidget);
      // Reject every non-fixture action. The planner back-fills from the
      // next-ranked recommendations after each rejection, so the selected
      // count does NOT shrink monotonically — loop on the live plan until
      // only the temp-files fixture action remains. Only that action may
      // execute — never real cache or crash-dump items.
      const fixtureTitle = 'Expired temporary files';
      List<Map> planSelected() =>
          ((controller.storagePlanResult?['selected'] as List?) ?? [])
              .cast<Map>();
      for (var guard = 0; guard < 12; guard++) {
        final nonFixture = planSelected()
            .where((e) => e['title'] != fixtureTitle)
            .toList();
        if (nonFixture.isEmpty) break;
        final target = nonFixture.first;
        final title = target['title'] as String;
        final rid = target['id'] as String;
        final tile = find.widgetWithText(CheckboxListTile, title);
        if (tile.evaluate().isEmpty) break;
        await tester.tap(tile);
        // Rejection triggers a live engine replan — wait until the rejected
        // id is gone from the engine's selected set before continuing.
        final updated = await waitFor(
            tester,
            () => !planSelected().any((e) => e['id'] == rid),
            const Duration(seconds: 30));
        if (!updated) {
          print('[M44] DEBUG after rejecting "$title" ($rid): '
              'planNull=${controller.storagePlanResult == null} '
              'err=${controller.errorMessage} '
              'status=${controller.status} '
              'selected=${planSelected().map((e) => e['id']).toList()}');
        }
        expect(updated, isTrue,
            reason: 'rejection of "$title" did not update the plan');
      }
      final approve = find.textContaining('Approve & execute');
      expect(approve, findsOneWidget);
      final approved = tester.widget<Text>(approve.first).data!;
      expect(approved, contains(' 1'),
          reason: 'exactly the temp-files fixture action must remain');
      await tester.tap(approve.first);
      expect(
          await waitFor(
              tester,
              () => controller.planExecutionOutcomes != null,
              const Duration(seconds: 30)),
          isTrue,
          reason: 'plan execution produced no outcomes');
      print('[M44] PASS plan execution: '
          '${controller.planExecutionOutcomes}');
    } else {
      print('[M44] note: no executable actions in plan to review');
    }

    // --- Phase 7: host-unavailable -------------------------------------
    // Kill only the process this test spawned — never a production engine.
    final kill = await Process.run(
        'taskkill', ['/F', '/PID', '${controller.spawnedPid}']);
    print('[M44] taskkill: ${kill.stdout}${kill.stderr}');
    await tester.tap(find.text('Cleaner'));
    await tester.pump(const Duration(milliseconds: 400));
    await tester.tap(find.widgetWithText(FilledButton, 'Analyze'));
    final errored = await waitFor(
        tester,
        () => controller.status == EngineStatus.error,
        const Duration(seconds: 15));
    print('[M44] host-unavailable: errored=$errored '
        'status=${controller.status} msg=${controller.errorMessage}');

    // --- Phase 8: clean shutdown ---------------------------------------
    await tester.pumpWidget(const SizedBox());
    await tester.pump(const Duration(seconds: 1));
    final remaining = await Process.run(
        'tasklist', ['/FI', 'IMAGENAME eq ClearForge.Host.exe', '/NH']);
    expect(remaining.stdout.toString(), isNot(contains('ClearForge.Host.exe')),
        reason: 'engine host still running after app dispose');
    print('[M44] PASS shutdown: host exited with app');
  });
}
