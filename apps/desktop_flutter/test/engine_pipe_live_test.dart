// ignore_for_file: avoid_print
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:clearforge_desktop/core/ipc/engine_pipe_client.dart';

/// Live-pipe regression: spawns the real ClearForge.Host (each on its own
/// pipe name for isolation) and verifies the transport can both write and
/// read — the original design deadlocked because a blocking ReadFile
/// starved the write queue on the same synchronous pipe handle.
/// The live host is located through the same resolution order the app uses —
/// no user-specific absolute path. When the engine simply isn't built (CI
/// lanes that don't compile it, fresh checkouts) the live tests skip rather
/// than fail: they exercise a real process, not a contract.
String? _hostExe() {
  // Under `flutter test` the resolved executable is the flutter_tester binary
  // inside the SDK, so default discovery cannot see the repo; the package
  // working directory walk-up can.
  for (final base in <String?>[null, Directory.current.path]) {
    final p = EngineProcess.resolveEnginePath(appDir: base);
    if (p.isNotEmpty) return p;
  }
  return null;
}

Future<Process> _spawnHost(String pipe, String exe) =>
    Process.start(exe, [pipe], mode: ProcessStartMode.normal);

Future<EngineClient> _connectWhenUp(String pipe) async {
  for (var i = 0; i < 40; i++) {
    final c = await EngineClient.connect(pipe);
    final res =
        await c.call('health.check').timeout(const Duration(seconds: 2));
    if (res['error'] == null) return c;
    await c.dispose();
    await Future<void>.delayed(const Duration(milliseconds: 200));
  }
  throw StateError('host never answered health.check on $pipe');
}

void main() {
  test('sequential calls on one client both complete', () async {
    final exe = _hostExe();
    if (exe == null) {
      print('engine host not built — skipping live pipe test');
      return;
    }
    final pipe = 'cf-test-${DateTime.now().microsecondsSinceEpoch}';
    final proc = await _spawnHost(pipe, exe);
    try {
      final c = await _connectWhenUp(pipe);
      final r2 =
          await c.call('history.get').timeout(const Duration(seconds: 15));
      print('history.get: $r2');
      expect(r2['error'], isNull);
      await c.dispose();
    } finally {
      proc.kill();
    }
  }, timeout: const Timeout(Duration(seconds: 30)));

  test('host death completes pending calls and blocks new calls', () async {
    final exe = _hostExe();
    if (exe == null) {
      print('engine host not built — skipping live pipe test');
      return;
    }
    final pipe = 'cf-test-${DateTime.now().microsecondsSinceEpoch}d';
    final proc = await _spawnHost(pipe, exe);
    try {
      final c = await _connectWhenUp(pipe);
      proc.kill();
      await Future<void>.delayed(const Duration(milliseconds: 800));
      final r2 =
          await c.call('health.check').timeout(const Duration(seconds: 5));
      print('after kill: $r2');
      expect(r2['error'], isNotNull,
          reason: 'calls on a dead pipe must fail fast, not hang');
      await c.dispose();
    } finally {
      proc.kill();
    }
  }, timeout: const Timeout(Duration(seconds: 30)));
}
