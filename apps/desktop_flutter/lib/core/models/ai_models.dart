// Client-side mirrors of the engine's models.* surface. The engine owns
// the catalogue and the model root; these objects only carry the public
// metadata and lifecycle state into the UI.

enum AiInstallState {
  notInstalled,
  downloading,
  interrupted,
  verifying,
  installing,
  ready,
  failed,
  removing,
  unknown,
}

AiInstallState _state(String? s) => switch (s) {
      'not_installed' => AiInstallState.notInstalled,
      'downloading' => AiInstallState.downloading,
      'interrupted' => AiInstallState.interrupted,
      'verifying' => AiInstallState.verifying,
      'installing' => AiInstallState.installing,
      'ready' => AiInstallState.ready,
      'failed' => AiInstallState.failed,
      'removing' => AiInstallState.removing,
      _ => AiInstallState.unknown,
    };

/// Live lifecycle state for one catalogue model.
class AiModelStatus {
  final String modelId;
  final AiInstallState state;
  final int? installedBytes;
  final int downloadedBytes;
  final int totalBytes;
  final String? error;

  const AiModelStatus({
    required this.modelId,
    required this.state,
    required this.installedBytes,
    required this.downloadedBytes,
    required this.totalBytes,
    this.error,
  });

  bool get isActive => switch (state) {
        AiInstallState.downloading ||
        AiInstallState.verifying ||
        AiInstallState.installing ||
        AiInstallState.removing =>
          true,
        _ => false,
      };

  factory AiModelStatus.fromJson(Map<String, dynamic> j) => AiModelStatus(
        modelId: j['modelId'] as String? ?? '',
        state: _state(j['state'] as String?),
        installedBytes: j['installedBytes'] as int?,
        downloadedBytes: j['downloadedBytes'] as int? ?? 0,
        totalBytes: j['totalBytes'] as int? ?? 0,
        error: j['error'] as String?,
      );
}

/// One curated, engine-approved model. The catalogue entry is authoritative
/// provenance — the UI never invents or accepts arbitrary model URLs.
class AiModelInfo {
  final String id;
  final String displayName;
  final String publisher;
  final String repository;
  final String? upstreamRepository;
  final String revision;
  final String fileName;
  final int sizeBytes;
  final String format;
  final String sha256;
  final String licenseId;
  final String licenseUrl;
  final String backend;
  final String acceleration;
  final List<String> architectures;
  final int minRamBytesEstimate;
  final int minFreeStorageBytesEstimate;
  final String supportStatus;
  final String qualityNotes;
  final AiModelStatus? status;

  const AiModelInfo({
    required this.id,
    required this.displayName,
    required this.publisher,
    required this.repository,
    this.upstreamRepository,
    required this.revision,
    required this.fileName,
    required this.sizeBytes,
    required this.format,
    required this.sha256,
    required this.licenseId,
    required this.licenseUrl,
    required this.backend,
    required this.acceleration,
    required this.architectures,
    required this.minRamBytesEstimate,
    required this.minFreeStorageBytesEstimate,
    required this.supportStatus,
    required this.qualityNotes,
    this.status,
  });

  AiModelInfo withStatus(AiModelStatus? s) => AiModelInfo(
        id: id,
        displayName: displayName,
        publisher: publisher,
        repository: repository,
        upstreamRepository: upstreamRepository,
        revision: revision,
        fileName: fileName,
        sizeBytes: sizeBytes,
        format: format,
        sha256: sha256,
        licenseId: licenseId,
        licenseUrl: licenseUrl,
        backend: backend,
        acceleration: acceleration,
        architectures: architectures,
        minRamBytesEstimate: minRamBytesEstimate,
        minFreeStorageBytesEstimate: minFreeStorageBytesEstimate,
        supportStatus: supportStatus,
        qualityNotes: qualityNotes,
        status: s,
      );

  factory AiModelInfo.fromJson(Map<String, dynamic> j) {
    final artifact = j['artifact'] as Map<String, dynamic>? ?? {};
    final license = j['license'] as Map<String, dynamic>? ?? {};
    final runtime = j['runtime'] as Map<String, dynamic>? ?? {};
    final req = j['requirements'] as Map<String, dynamic>? ?? {};
    return AiModelInfo(
      id: j['id'] as String? ?? '',
      displayName: j['displayName'] as String? ?? '',
      publisher: j['publisher'] as String? ?? '',
      repository: j['repository'] as String? ?? '',
      upstreamRepository: j['upstreamRepository'] as String?,
      revision: j['revision'] as String? ?? '',
      fileName: artifact['fileName'] as String? ?? '',
      sizeBytes: artifact['sizeBytes'] as int? ?? 0,
      format: artifact['format'] as String? ?? '',
      sha256: artifact['sha256'] as String? ?? '',
      licenseId: license['id'] as String? ?? '',
      licenseUrl: license['url'] as String? ?? '',
      backend: runtime['backend'] as String? ?? '',
      acceleration: runtime['acceleration'] as String? ?? '',
      architectures:
          (runtime['architectures'] as List<dynamic>? ?? []).cast<String>(),
      minRamBytesEstimate: req['minRamBytesEstimate'] as int? ?? 0,
      minFreeStorageBytesEstimate:
          req['minFreeStorageBytesEstimate'] as int? ?? 0,
      supportStatus: j['supportStatus'] as String? ?? 'unsupported',
      qualityNotes: j['qualityNotes'] as String? ?? '',
      status: j['state'] is Map<String, dynamic>
          ? AiModelStatus.fromJson(j['state'] as Map<String, dynamic>)
          : null,
    );
  }
}
