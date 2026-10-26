import 'dart:async';

import 'package:flutter/material.dart';

import '../../core/models/ai_models.dart';
import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

/// Curated AI model catalogue: engine-published entries only — there is no
/// arbitrary URL or free-form model input anywhere in this UI. Downloads
/// happen once, on explicit user action, from the pinned upstream artifact.
class ModelsScreen extends StatefulWidget {
  final EngineController controller;
  const ModelsScreen({super.key, required this.controller});

  @override
  State<ModelsScreen> createState() => _ModelsScreenState();
}

class _ModelsScreenState extends State<ModelsScreen> {
  @override
  void initState() {
    super.initState();
    unawaited(widget.controller.loadModelCatalog());
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.controller;
    return ListenableBuilder(
      listenable: c,
      builder: (context, _) {
        return Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Wrap(spacing: 16, crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    Text('AI Models',
                        style: Theme.of(context).textTheme.titleLarge),
                    IconButton(
                      icon: const Icon(Icons.refresh),
                      tooltip: 'Refresh',
                      onPressed: c.modelsLoading
                          ? null
                          : () => unawaited(c.loadModelCatalog()),
                    ),
                  ]),
              Text(
                'Curated local models only. Each artifact is downloaded once '
                'from its pinned upstream revision and verified by SHA-256 '
                'before install — nothing runs, extracts, or phones home.',
                style: Theme.of(context).textTheme.bodySmall,
              ),
              const SizedBox(height: 16),
              Expanded(
                child: c.aiModels.isEmpty
                    ? Center(
                        child: Text(c.modelsLoading
                            ? 'Loading catalogue…'
                            : 'No catalogue available — is the engine running?'))
                    : ListView(
                        children: [
                          for (final m in c.aiModels)
                            _ModelCard(model: m, controller: c),
                        ],
                      ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _ModelCard extends StatelessWidget {
  final AiModelInfo model;
  final EngineController controller;
  const _ModelCard({required this.model, required this.controller});

  @override
  Widget build(BuildContext context) {
    final s = model.status;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Expanded(
                child: Text(model.displayName,
                    style: Theme.of(context).textTheme.titleMedium),
              ),
              _SupportChip(status: model.supportStatus),
            ]),
            Text('${model.publisher} · ${model.repository} · '
                'rev ${model.revision.substring(0, 7)}',
                style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 8),
            Wrap(spacing: 16, runSpacing: 4, children: [
              Text('Size: ${formatBytes(model.sizeBytes)}'),
              Text('Format: ${model.format}'),
              Text('Runtime: ${model.backend} (${model.acceleration})'),
              Text('Arch: ${model.architectures.join(', ')}'),
            ]),
            Wrap(spacing: 16, runSpacing: 4, children: [
              Text('Licence: ${model.licenseId}'),
              Text('RAM (est.): ≥ ${formatBytes(model.minRamBytesEstimate)}'),
              Text('Disk (est.): ≥ ${formatBytes(model.minFreeStorageBytesEstimate)}'),
            ]),
            const SizedBox(height: 8),
            Text(model.qualityNotes,
                style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 8),
            _StateArea(model: model, controller: controller),
            if (s?.error != null)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text('${s!.error}',
                    style: TextStyle(
                        color: Theme.of(context).colorScheme.error,
                        fontSize: 12)),
              ),
          ],
        ),
      ),
    );
  }
}

class _SupportChip extends StatelessWidget {
  final String status;
  const _SupportChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final (color, label) = switch (status) {
      'recommended' => (Colors.green, 'recommended'),
      'experimental' => (Colors.orange, 'experimental'),
      _ => (Colors.grey, status),
    };
    return Chip(
      avatar: Icon(Icons.circle, size: 10, color: color),
      label: Text(label),
      visualDensity: VisualDensity.compact,
    );
  }
}

class _StateArea extends StatelessWidget {
  final AiModelInfo model;
  final EngineController controller;
  const _StateArea({required this.model, required this.controller});

  @override
  Widget build(BuildContext context) {
    final s = model.status;
    final state = s?.state ?? AiInstallState.notInstalled;
    return switch (state) {
      AiInstallState.ready => Row(children: [
          const Icon(Icons.verified, size: 18, color: Colors.green),
          const SizedBox(width: 6),
          const Text('Installed · integrity verified'),
          const Spacer(),
          TextButton(
            onPressed: () => unawaited(controller.unloadModel()),
            child: const Text('Unload'),
          ),
          const SizedBox(width: 8),
          OutlinedButton.icon(
            icon: const Icon(Icons.delete_outline),
            label: const Text('Remove'),
            onPressed: () => _confirmRemove(context),
          ),
        ]),
      AiInstallState.downloading => Column(
          crossAxisAlignment: CrossAxisAlignment.start, children: [
          LinearProgressIndicator(
              value: s!.totalBytes > 0
                  ? s.downloadedBytes / s.totalBytes
                  : null),
          const SizedBox(height: 4),
          Row(children: [
            Text('Downloading ${formatBytes(s.downloadedBytes)} '
                'of ${formatBytes(s.totalBytes)}'),
            const Spacer(),
            TextButton(
                onPressed: () =>
                    unawaited(controller.cancelModelInstall(model.id)),
                child: const Text('Cancel')),
          ]),
        ]),
      AiInstallState.interrupted => Column(
          crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('Paused — ${formatBytes(s?.downloadedBytes ?? 0)} of '
              '${formatBytes(s?.totalBytes ?? model.sizeBytes)} retained '
              'for resume.'),
          const SizedBox(height: 4),
          Row(children: [
            FilledButton.icon(
              icon: const Icon(Icons.download),
              label: const Text('Resume download'),
              onPressed: () => unawaited(controller.installModel(model.id)),
            ),
            const SizedBox(width: 8),
            OutlinedButton.icon(
              icon: const Icon(Icons.delete_outline),
              label: const Text('Discard'),
              onPressed: () => _confirmRemove(context),
            ),
          ]),
        ]),
      AiInstallState.verifying =>
        const Text('Verifying SHA-256…'),
      AiInstallState.installing =>
        const Text('Installing…'),
      AiInstallState.removing => const Text('Removing…'),
      AiInstallState.failed => Row(children: [
          Expanded(child: Text('Failed: ${s?.error ?? "unknown error"}')),
          FilledButton.icon(
            icon: const Icon(Icons.refresh),
            label: const Text('Retry'),
            onPressed: () => unawaited(controller.installModel(model.id)),
          ),
        ]),
      _ => FilledButton.icon(
          icon: const Icon(Icons.download),
          label: Text('Download ${formatBytes(model.sizeBytes)}'),
          onPressed: () => unawaited(controller.installModel(model.id)),
        ),
    };
  }

  Future<void> _confirmRemove(BuildContext context) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Remove model?'),
        content: const Text(
            'The local artifact will be deleted from ClearForge storage. '
            'The AI worker is unloaded first; you can re-download later.'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Cancel')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Remove')),
        ],
      ),
    );
    if (ok == true) unawaited(controller.removeModel(model.id));
  }
}
