import 'dart:io';

import '../ipc/engine_pipe_client.dart';

/// Dedicated AI worker lifecycle — lazily spawned only when the curated
/// model artifact is present, fully killed on dispose. A crash here can
/// never take down the engine host: it is a separate process on its own
/// pipe with no access to engine APIs.
class AiWorker {
  static const pipeName = 'clearforge.ai.v1';
  static const defaultModelFileName = 'Qwen2.5-1.5B-Instruct-Q4_K_M.gguf';
  static const defaultModelId = 'qwen2.5-1.5b-instruct-q4_k_m';

  Process? _proc;
  EngineClient? _client;
  bool _modelLoadAttempted = false;
  String? lastError;

  /// Set by the controller from the engine catalogue — the worker only ever
  /// loads a curated, engine-verified artifact name, never a UI-supplied
  /// path.
  String modelFileName = defaultModelFileName;
  String modelId = defaultModelId;

  /// The curated model must already exist under the dedicated model root —
  /// the worker is never started when it is absent.
  File get modelFile => File(
      '${Platform.environment['LOCALAPPDATA']}\\ClearForge\\models\\$modelFileName');

  bool get modelPresent => modelFile.existsSync();
  bool get connected => _client != null;

  static String defaultExePath() {
    var dir = File(Platform.resolvedExecutable).parent;
    for (var i = 0; i < 8; i++) {
      final candidate = File(
          '${dir.path}\\native\\ClearForge.Ai\\bin\\Debug\\net8.0\\ClearForge.Ai.exe');
      if (candidate.existsSync()) return candidate.path;
      dir = dir.parent;
    }
    return '';
  }

  static Future<Process> spawn({String? exePath}) {
    final path = exePath ?? defaultExePath();
    if (path.isEmpty) {
      throw StateError(
          'ClearForge.Ai.exe not found; build the AI worker first.');
    }
    return Process.start(path, const [], mode: ProcessStartMode.normal);
  }

  /// Lazy connect — spawn only when the model exists and the worker binary
  /// is available. Bounded retries while the pipe instance comes up.
  Future<bool> ensureConnected() async {
    if (_client != null) return true;
    if (!modelPresent) {
      lastError = 'model not installed';
      return false;
    }
    try {
      _proc ??= await spawn();
    } on StateError catch (e) {
      lastError = e.message;
      return false;
    }
    for (var i = 0; i < 20; i++) {
      try {
        _client = await EngineClient.connect(pipeName);
        lastError = null;
        return true;
      } catch (_) {
        await Future<void>.delayed(const Duration(milliseconds: 250));
      }
    }
    lastError = 'worker startup timed out';
    return false;
  }

  /// Load the curated model exactly once per worker lifetime.
  Future<Map<String, dynamic>?> _ensureModelLoaded() async {
    if (_modelLoadAttempted) return {'status': 'already_loaded'};
    _modelLoadAttempted = true;
    final res = await _client!.call('ai.model.load', {'modelId': modelId});
    if (res['error'] != null) {
      lastError = res['error']?['message'] as String? ?? 'model load failed';
      return null;
    }
    return res['result'] as Map<String, dynamic>?;
  }

  /// Unload the model in the worker, then tear the worker down — the
  /// engine-side remover requires the artifact not be held open. Safe to
  /// call when nothing is running.
  Future<void> shutdownForRemoval() async {
    try {
      await _client?.call('ai.model.unload');
    } catch (_) {}
    await dispose();
    _modelLoadAttempted = false;
  }

  /// The one bounded task: natural-language goal + bounded recommendation
  /// summary → validated intent for the deterministic planner.
  Future<Map<String, dynamic>?> interpretGoal(
      String goal, List<Map<String, dynamic>> recommendations) async {
    if (!await ensureConnected()) return null;
    if (await _ensureModelLoaded() == null) return null;
    final res = await _client!.call('ai.infer', {
      'goal': goal,
      'recommendations': recommendations,
    });
    if (res['error'] != null) {
      lastError = res['error']?['message'] as String? ?? 'inference failed';
      return null;
    }
    lastError = null;
    return res['result'] as Map<String, dynamic>?;
  }

  Future<void> dispose() async {
    await _client?.dispose();
    _client = null;
    _proc?.kill();
    _proc = null;
  }
}
