import 'package:clearforge_desktop/core/state/engine_controller.dart';
import 'package:clearforge_desktop/features/apps/apps_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// M8.4b leftovers surface: seeded candidates render in the review dialog;
/// the surface is review-only (no selection/commit affordance exists).
void main() {
  testWidgets('app tile offers leftovers; dialog shows review-only evidence',
      (tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 800));
    tester.view.physicalSize = const Size(1280, 800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final c = EngineController()
      ..status = EngineStatus.ready
      ..apps = [
        {
          'name': 'Toolbox', 'publisher': 'Contoso', 'version': '2.1',
          'source': 'registry-hkcu', 'scope': 'current-user', 'kind': 'app',
          'hasUninstaller': true, 'confidence': 'registered',
        }
      ]
      ..appsNote = 'read-only';

    await tester.pumpWidget(MaterialApp(
        home: Scaffold(body: AppsScreen(controller: c))));
    await tester.pump();

    await tester.tap(find.byIcon(Icons.find_in_page_outlined));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));

    // Dialog is up, review-only framing present.
    expect(find.textContaining('Leftovers — '), findsOneWidget);
    expect(find.textContaining('Review evidence only'), findsOneWidget);

    // Seed results as if the call completed.
    c.leftoversLoading = false;
    c.appLeftovers = [
      {
        'path': 'C:\\Users\\me\\AppData\\Roaming\\Contoso',
        'root': 'appdata-roaming',
        'matchBasis': 'publisher',
        'confidence': 'publisher-context',
        'reason': "directory 'Contoso' matches app publisher",
      },
    ];
    // ignore: invalid_use_of_protected_member, invalid_use_of_visible_for_testing_member
    c.notifyListeners();
    await tester.pump();

    expect(find.textContaining('appdata-roaming · publisher'), findsOneWidget);
    // No executable affordance anywhere in the dialog.
    expect(find.text('Quarantine'), findsNothing);
    expect(find.text('Delete'), findsNothing);
  });
}
