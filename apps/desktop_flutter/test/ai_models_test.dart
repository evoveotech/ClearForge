import 'package:clearforge_desktop/core/models/ai_models.dart';
import 'package:flutter_test/flutter_test.dart';

Map<String, dynamic> sampleModel({String state = 'not_installed'}) => {
      'id': 'qwen2.5-1.5b-instruct-q4_k_m',
      'displayName': 'Qwen2.5 1.5B Instruct (Q4_K_M)',
      'publisher': 'bartowski',
      'repository': 'bartowski/Qwen2.5-1.5B-Instruct-GGUF',
      'upstreamRepository': 'Qwen/Qwen2.5-1.5B-Instruct',
      'revision': '9eadc66189c7641e1ddd226b8267a9119b2ce2d4',
      'artifact': {
        'fileName': 'Qwen2.5-1.5B-Instruct-Q4_K_M.gguf',
        'sizeBytes': 986048768,
        'format': 'gguf',
        'sha256': '1adf0b11065d8ad2e8123ea110d1ec956dab4ab038eab665614adba04b6c3370',
      },
      'license': {'id': 'apache-2.0', 'url': 'https://www.apache.org/licenses/LICENSE-2.0'},
      'runtime': {
        'backend': 'llamasharp/0.30.0 llama.cpp',
        'acceleration': 'cpu',
        'architectures': ['x64'],
      },
      'requirements': {
        'minRamBytesEstimate': 3221225472,
        'minFreeStorageBytesEstimate': 2147483648,
        'estimatesAreApproximate': true,
      },
      'supportStatus': 'recommended',
      'qualityNotes': 'provenance-verified',
      'state': {
        'modelId': 'qwen2.5-1.5b-instruct-q4_k_m',
        'state': state,
        'installedBytes': state == 'ready' ? 986048768 : null,
        'downloadedBytes': 0,
        'totalBytes': 986048768,
        'error': null,
      },
    };

void main() {
  group('AiModelInfo.fromJson', () {
    test('parses a complete catalogue entry with state', () {
      final m = AiModelInfo.fromJson(sampleModel(state: 'ready'));
      expect(m.id, 'qwen2.5-1.5b-instruct-q4_k_m');
      expect(m.sizeBytes, 986048768);
      expect(m.sha256, hasLength(64));
      expect(m.licenseId, 'apache-2.0');
      expect(m.supportStatus, 'recommended');
      expect(m.status?.state, AiInstallState.ready);
      expect(m.status?.installedBytes, 986048768);
    });

    test('missing state is tolerated, not fabricated', () {
      final j = sampleModel()..remove('state');
      final m = AiModelInfo.fromJson(j);
      expect(m.status, isNull);
    });
  });

  group('AiModelStatus', () {
    test('state names map to the lifecycle enum', () {
      AiModelStatus mk(String s) => AiModelStatus.fromJson({
            'modelId': 'm', 'state': s, 'installedBytes': null,
            'downloadedBytes': 0, 'totalBytes': 0,
          });
      expect(mk('not_installed').state, AiInstallState.notInstalled);
      expect(mk('downloading').state, AiInstallState.downloading);
      expect(mk('interrupted').state, AiInstallState.interrupted);
      expect(mk('verifying').state, AiInstallState.verifying);
      expect(mk('installing').state, AiInstallState.installing);
      expect(mk('ready').state, AiInstallState.ready);
      expect(mk('failed').state, AiInstallState.failed);
      expect(mk('removing').state, AiInstallState.removing);
      expect(mk('bogus').state, AiInstallState.unknown);
    });

    test('isActive only for in-flight lifecycle states', () {
      AiModelStatus mk(String s) => AiModelStatus.fromJson({
            'modelId': 'm', 'state': s, 'installedBytes': null,
            'downloadedBytes': 0, 'totalBytes': 0,
          });
      expect(mk('downloading').isActive, isTrue);
      expect(mk('verifying').isActive, isTrue);
      expect(mk('installing').isActive, isTrue);
      expect(mk('removing').isActive, isTrue);
      expect(mk('ready').isActive, isFalse);
      expect(mk('interrupted').isActive, isFalse);
      expect(mk('failed').isActive, isFalse);
      expect(mk('not_installed').isActive, isFalse);
    });
  });
}
