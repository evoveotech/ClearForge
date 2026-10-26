import 'package:clearforge_desktop/core/models/scan_models.dart';
import 'package:clearforge_desktop/core/ipc/engine_pipe_client.dart';
import 'package:flutter_test/flutter_test.dart';
import 'dart:convert';

void main() {
  group('ScanCandidate.fromJson', () {
    test('parses a complete candidate', () {
      final c = ScanCandidate.fromJson(const {
        'id': 's1:0',
        'ruleId': 'user-temp-expired-files',
        'category': 'temporary-files',
        'displayName': 'a.tmp',
        'estimatedBytes': 42,
        'risk': 'low',
        'reason': 'matched rule user-temp-expired-files v1',
        'requiresExplicitConfirmation': true,
      });
      expect(c.id, 's1:0');
      expect(c.risk, RiskLevel.low);
      expect(c.estimatedBytes, 42);
      expect(c.requiresExplicitConfirmation, isTrue);
    });

    test('unknown risk maps to restricted, never less', () {
      final c = ScanCandidate.fromJson(const {
        'id': 'x', 'risk': 'mystery', 'displayName': 'f', 'estimatedBytes': 1,
      });
      expect(c.risk, RiskLevel.restricted);
    });
  });

  group('ScanResults', () {
    test('isTerminal only for terminal statuses', () {
      ScanResults mk(String s) => ScanResults(
          sessionId: 's', status: s, estimatedBytes: 0,
          categories: const {}, candidates: const []);
      expect(mk('scanning').isTerminal, isFalse);
      expect(mk('completed').isTerminal, isTrue);
      expect(mk('cancelled').isTerminal, isTrue);
      expect(mk('failed').isTerminal, isTrue);
    });
  });

  group('wire framing', () {
    test('encodeRequestFrame produces newline-terminated JSON', () {
      final bytes = encodeRequestFrame({'id': '1', 'method': 'health.check'});
      final s = utf8.decode(bytes);
      expect(s.endsWith('\n'), isTrue);
      expect(jsonDecode(s.trim())['method'], 'health.check');
    });

    test('decodeResponseLine tolerates blanks and rejects non-objects', () {
      expect(decodeResponseLine(''), isNull);
      expect(decodeResponseLine('   '), isNull);
      expect(decodeResponseLine('[1,2]'), isNull);
      expect(decodeResponseLine('{"id":"1","result":{}}')!['id'], '1');
    });
  });

  group('formatBytes', () {
    test('formats magnitudes', () {
      expect(formatBytes(512), '512 B');
      expect(formatBytes(2048), '2.0 KB');
      expect(formatBytes(5 * 1024 * 1024), '5.0 MB');
    });
  });
}
