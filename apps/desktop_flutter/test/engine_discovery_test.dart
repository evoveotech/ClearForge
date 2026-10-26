import 'package:flutter_test/flutter_test.dart';
import 'package:clearforge_desktop/core/ipc/engine_pipe_client.dart';

// REL-1: engine-path resolution must not assume the Debug dev-tree layout —
// packaged installs place the host beside the app, and a Release engine must
// never lose to a stale Debug build at the same repo root.

void main() {
  group('enginePathCandidates', () {
    test('packaged layout is checked first', () {
      final c = EngineProcess.enginePathCandidates(r'C:\Apps\ClearForge');
      expect(c[0], r'C:\Apps\ClearForge\engine\ClearForge.Host.exe');
      expect(c[1], r'C:\Apps\ClearForge\ClearForge.Host.exe');
    });

    test('dev-tree walk emits Release before Debug at each level', () {
      final c = EngineProcess.enginePathCandidates(r'C:\repo\app');
      expect(
          c[2],
          r'C:\repo\app\native\ClearForge.Host\bin\Release\net8.0'
          r'\ClearForge.Host.exe');
      expect(
          c[3],
          r'C:\repo\app\native\ClearForge.Host\bin\Debug\net8.0'
          r'\ClearForge.Host.exe');
    });

    test('walk ascends ancestors up to the bound', () {
      final c = EngineProcess.enginePathCandidates(r'C:\a\b\c');
      expect(
          c,
          contains(r'C:\a\native\ClearForge.Host\bin\Release\net8.0'
              r'\ClearForge.Host.exe'));
      expect(c.where((p) => p.contains(r'native\')).length, lessThanOrEqualTo(16));
    });
  });

  group('resolveEnginePath', () {
    bool never(String _) => false;

    test('explicit exePath wins over everything', () {
      final p = EngineProcess.resolveEnginePath(
        exePath: r'D:\explicit\ClearForge.Host.exe',
        envPath: r'D:\env\ClearForge.Host.exe',
        exists: never,
      );
      expect(p, r'D:\explicit\ClearForge.Host.exe');
    });

    test('env var wins over discovery and is trusted as-is', () {
      final p = EngineProcess.resolveEnginePath(
        envPath: r'D:\env\ClearForge.Host.exe',
        exists: never,
      );
      expect(p, r'D:\env\ClearForge.Host.exe');
    });

    test('packaged engine is preferred over a dev-tree hit', () {
      final p = EngineProcess.resolveEnginePath(
        appDir: r'C:\Apps\ClearForge',
        exists: (path) => path == r'C:\Apps\ClearForge\engine\ClearForge.Host.exe'
            || path.contains(r'bin\Debug'),
      );
      expect(p, r'C:\Apps\ClearForge\engine\ClearForge.Host.exe');
    });

    test('release build wins over debug at the same root', () {
      final p = EngineProcess.resolveEnginePath(
        appDir: r'C:\repo\app',
        exists: (path) => path.startsWith(r'C:\repo\app\native'),
      );
      expect(p, contains(r'bin\Release'));
    });

    test('walk-up finds a repo-root dev build', () {
      final p = EngineProcess.resolveEnginePath(
        appDir: r'C:\repo\apps\desktop_flutter\build\windows\x64\runner\Debug',
        exists: (path) =>
            path ==
            r'C:\repo\native\ClearForge.Host\bin\Debug\net8.0'
            r'\ClearForge.Host.exe',
      );
      expect(
          p,
          r'C:\repo\native\ClearForge.Host\bin\Debug\net8.0'
          r'\ClearForge.Host.exe');
    });

    test('returns empty when nothing exists', () {
      final p = EngineProcess.resolveEnginePath(
        appDir: r'C:\nowhere',
        exists: never,
      );
      expect(p, isEmpty);
    });
  });
}
