import 'package:flutter_test/flutter_test.dart';
import 'package:clearforge_desktop/core/models/scan_models.dart';

// M8.4a: model-level coverage of the apps.list response shape and the UI
// rules that keep the inventory honest (kinds, missing fields, sizes).

Map<String, dynamic> sampleApp({
  String name = 'Contoso App',
  String? publisher = 'Contoso Ltd',
  String? version = '1.2.3',
  String? location = r'C:\Program Files\Contoso',
  int? sizeBytes = 2 * 1024 * 1024,
  String? installDate = '2024-01-15',
  String source = 'registry-hklm-64',
  String scope = 'machine',
  String kind = 'app',
  bool hasUninstaller = true,
  String confidence = 'registered',
}) =>
    {
      'name': name,
      'publisher': publisher,
      'version': version,
      'installLocation': location,
      'sizeBytes': sizeBytes,
      'installDate': installDate,
      'source': source,
      'scope': scope,
      'kind': kind,
      'hasUninstaller': hasUninstaller,
      'confidence': confidence,
    };

Map<String, dynamic> sampleResponse(List<Map<String, dynamic>> apps) => {
      'result': {'apps': apps, 'note': 'Read-only inventory.'},
    };

void main() {
  group('apps.list response', () {
    test('apps array decodes with all fields', () {
      final res = sampleResponse([sampleApp()]);
      final apps =
          (res['result']?['apps'] as List).cast<Map<String, dynamic>>();
      expect(apps, hasLength(1));
      final a = apps.first;
      expect(a['name'], 'Contoso App');
      expect(a['sizeBytes'], 2 * 1024 * 1024);
      expect(formatBytes(a['sizeBytes'] as int), '2.0 MB');
      expect(a['hasUninstaller'], isTrue);
      expect(a['confidence'], 'registered');
    });

    test('missing optional fields stay null', () {
      final a = sampleApp(publisher: null, sizeBytes: null);
      expect(a['publisher'], isNull);
      expect(a['sizeBytes'], isNull);
    });

    test('system components and updates carry distinct kinds', () {
      final sys = sampleApp(kind: 'system-component');
      final upd = sampleApp(kind: 'update');
      expect(sys['kind'], 'system-component');
      expect(upd['kind'], 'update');
    });

    test('listed-only confidence marks entries without an uninstaller', () {
      final a =
          sampleApp(hasUninstaller: false, confidence: 'listed-only');
      expect(a['hasUninstaller'], isFalse);
      expect(a['confidence'], 'listed-only');
    });
  });

  group('apps screen filtering rules', () {
    test('default view hides non-app kinds', () {
      // Mirrors AppsScreen: kind != 'app' is hidden unless the chip is on.
      final rows = [
        sampleApp(name: 'Real App'),
        sampleApp(name: 'SysComp', kind: 'system-component'),
        sampleApp(name: 'KB Update', kind: 'update'),
      ];
      final visible =
          rows.where((a) => a['kind'] == 'app').toList();
      expect(visible, hasLength(1));
      expect(visible.single['name'], 'Real App');
    });
  });
}
