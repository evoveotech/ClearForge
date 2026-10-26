// ignore_for_file: avoid_print
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:clearforge_desktop/core/ipc/engine_pipe_client.dart';

/// Live-pipe regression: spawns the real ClearForge.Host (each on its own
/// pipe name for isolation) and verifies the transport can both write and
/// read — the original design deadlocked because a blocking ReadFile
/// starved the write queue on the same synchronous pipe handle.
const hostExe =
    r'C:\Users\hardi\IdeaProjects\HardiksTool\ClearForgeData\native\ClearForge.Host\bin\Debug\net8.0\ClearForge.Host.exe';

Future<Process> _spawnHost(String pipe) =>
    Process.start(hostExe, [pipe], mode: ProcessStartMode.normal);

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
    final pipe = 'cf-test-${DateTime.now().microsecondsSinceEpoch}';
    final proc = await _spawnHost(pipe);
    try {
      final c = await _connectWhenUp(pipe);
      final r2 =
          await c.call('history.get').timeout(const Duration(seconds: 5));
      print('history.get: $r2');
      expect(r2['error'], isNull);
      await c.dispose();
    } finally {
      proc.kill();
    }
  }, timeout: const Timeout(Duration(seconds: 30)));

  test('host death completes pending calls and blocks new calls', () async {
    final pipe = 'cf-test-${DateTime.now().microsecondsSinceEpoch}d';
    final proc = await _spawnHost(pipe);
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
