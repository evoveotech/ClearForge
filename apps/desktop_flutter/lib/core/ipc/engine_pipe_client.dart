import 'dart:async';
import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';

import 'package:ffi/ffi.dart';
import 'package:win32/win32.dart';

/// Pure framing helpers, unit-testable without a pipe.
List<int> encodeRequestFrame(Map<String, dynamic> request) =>
    utf8.encode('${jsonEncode(request)}\n');

Map<String, dynamic>? decodeResponseLine(String line) {
  final trimmed = line.trim();
  if (trimmed.isEmpty) return null;
  final decoded = jsonDecode(trimmed);
  return decoded is Map<String, dynamic> ? decoded : null;
}

/// Win32 named-pipe transport owned by a background isolate so pipe I/O
/// never stalls the UI thread.
///
/// The handle is opened in synchronous (non-overlapped) mode, which means
/// the kernel serializes operations on the file object: a WriteFile issued
/// while a blocking ReadFile is pending waits for the read to complete —
/// and the read only completes after the host answers a request it can
/// never receive. To avoid that deadlock the worker polls with
/// PeekNamedPipe and only calls ReadFile once bytes are already available,
/// keeping its event loop free to service the write queue.
class EnginePipeTransport {
  final Isolate _isolate;
  final SendPort _toWorker;
  final StreamController<String> _lines = StreamController<String>.broadcast();

  EnginePipeTransport._(this._isolate, this._toWorker);

  static Future<EnginePipeTransport> connect(String pipeName) async {
    final ready = ReceivePort();
    final inbound = ReceivePort();
    final isolate = await Isolate.spawn(
        _worker, _WorkerArgs(ready.sendPort, inbound.sendPort, pipeName));
    final toWorker = await ready.first as SendPort;

    final t = EnginePipeTransport._(isolate, toWorker);
    inbound.listen((m) {
      if (!t._lines.isClosed) t._lines.add(m as String);
    });
    return t;
  }

  Stream<String> get lines => _lines.stream;

  /// Sends one complete JSON line; the worker appends the newline terminator.
  void writeLine(String json) => _toWorker.send(json);

  Future<void> dispose() async {
    _toWorker.send(null); // shutdown
    // The worker exits its poll loop within ~5ms and closes the handle;
    // kill is only a fallback for a stuck native call.
    await Future<void>.delayed(const Duration(milliseconds: 50));
    _isolate.kill(priority: Isolate.immediate);
    await _lines.close();
  }
}

class _WorkerArgs {
  final SendPort ready;
  final SendPort inbound;
  final String pipeName;
  _WorkerArgs(this.ready, this.inbound, this.pipeName);
}

void _worker(_WorkerArgs args) async {
  final commands = ReceivePort();
  args.ready.send(commands.sendPort);

  final name = ('\\\\.\\pipe\\${args.pipeName}').toPcwstr(allocator: calloc);
  // The accept loop keeps only one pending instance, so CreateFile races can
  // land in a transient ERROR_PIPE_BUSY window — retry through it.
  Win32Result<HANDLE>? res;
  for (var tries = 0; tries < 50; tries++) {
    res = CreateFile(
        name,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_NONE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        null);
    if (res.value.address != INVALID_HANDLE_VALUE.address) break;
    if (res.error != 231 /* ERROR_PIPE_BUSY */) break;
    await Future<void>.delayed(const Duration(milliseconds: 100));
  }
  calloc.free(name);
  final hFile = res!.value;
  if (hFile.address == INVALID_HANDLE_VALUE.address) {
    args.inbound.send('__error__:${res.error}');
    return;
  }

  var running = true;
  commands.listen((line) {
    if (line == null) {
      running = false;
      return;
    }
    final bytes = utf8.encode('$line\n');
    final ptr = calloc<Uint8>(bytes.length);
    ptr.asTypedList(bytes.length).setAll(0, bytes);
    final written = calloc<Uint32>();
    final w = WriteFile(hFile, ptr, bytes.length, written, nullptr);
    calloc.free(ptr);
    calloc.free(written);
    if (!w.value) args.inbound.send('__error__:${w.error}');
  });

  // Poll for inbound data; emits newline-delimited frames to the main
  // isolate. ReadFile only runs once PeekNamedPipe reports bytes present,
  // so it returns immediately and can never sit pending behind writes.
  final buf = calloc<Uint8>(65536);
  final read = calloc<Uint32>();
  final avail = calloc<Uint32>();
  final pending = StringBuffer();
  while (running) {
    final p = PeekNamedPipe(hFile, nullptr, 0, nullptr, avail, nullptr);
    if (!p.value) {
      args.inbound.send('__error__:${p.error}');
      break;
    }
    if (avail.value == 0) {
      await Future<void>.delayed(const Duration(milliseconds: 5));
      continue;
    }
    final r = ReadFile(hFile, buf, 65536, read, nullptr);
    if (!r.value || read.value == 0) {
      args.inbound.send('__error__:${r.error}');
      break;
    }
    pending.write(utf8.decode(buf.asTypedList(read.value)));
    final text = pending.toString();
    var start = 0;
    for (var i = 0; i < text.length; i++) {
      if (text[i] == '\n') {
        args.inbound.send(text.substring(start, i));
        start = i + 1;
      }
    }
    pending
      ..clear()
      ..write(text.substring(start));
  }
  CloseHandle(hFile);
  calloc.free(buf);
  calloc.free(read);
  calloc.free(avail);
}

/// Request/response client over [EnginePipeTransport] with id correlation.
class EngineClient {
  final EnginePipeTransport _transport;
  final _pending = <String, Completer<Map<String, dynamic>>>{};
  int _next = 0;
  bool _closed = false;
  StreamSubscription<String>? _sub;

  EngineClient._(this._transport) {
    _sub = _transport.lines.listen(_onLine);
  }

  static Future<EngineClient> connect(String pipeName) async =>
      EngineClient._(await EnginePipeTransport.connect(pipeName));

  /// Transport failures are reported as ordinary error responses so every
  /// call site keeps working through its existing `res['error']` handling —
  /// a dead pipe must never leave a request hanging.
  static Map<String, dynamic> _errorResponse(String detail) =>
      {'error': {'code': 'transport_closed', 'message': 'engine pipe closed: $detail'}};

  void _onLine(String line) {
    if (line.startsWith('__error__')) {
      _closed = true;
      for (final c in _pending.values) {
        c.complete(_errorResponse(line));
      }
      _pending.clear();
      return;
    }
    final msg = decodeResponseLine(line);
    if (msg == null) return;
    final id = msg['id'] as String?;
    if (id != null && _pending.containsKey(id)) {
      _pending.remove(id)!.complete(msg);
    }
  }

  Future<Map<String, dynamic>> call(String method,
      [Map<String, dynamic>? params]) {
    if (_closed) return Future.value(_errorResponse('connection lost'));
    final id = 'req-${_next++}';
    final c = Completer<Map<String, dynamic>>();
    _pending[id] = c;
    _transport.writeLine(
        jsonEncode({'id': id, 'method': method, if (params != null) ...{'params': params}}));
    return c.future;
  }

  Future<void> dispose() async {
    await _sub?.cancel();
    await _transport.dispose();
  }
}

/// Locates and launches the engine host process.
class EngineProcess {
  static String defaultExePath() {
    var dir = File(Platform.resolvedExecutable).parent;
    for (var i = 0; i < 8; i++) {
      final candidate = File(
          '${dir.path}\\native\\ClearForge.Host\\bin\\Debug\\net8.0\\ClearForge.Host.exe');
      if (candidate.existsSync()) return candidate.path;
      dir = dir.parent;
    }
    return '';
  }

  static Future<Process> spawn({String? exePath, String? pipeName}) {
    final path = exePath ?? defaultExePath();
    if (path.isEmpty) {
      throw StateError('ClearForge.Host.exe not found; build the engine first '
          '(dotnet build native/ClearForge.sln).');
    }
    return Process.start(path, [?pipeName],
        mode: ProcessStartMode.normal);
  }
}
