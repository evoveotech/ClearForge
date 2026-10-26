import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

class CleanerScreen extends StatelessWidget {
  final EngineController controller;
  const CleanerScreen({super.key, required this.controller});

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) {
        final results = controller.results;
        return Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  FilledButton.icon(
                    onPressed: controller.status == EngineStatus.ready
                        ? controller.startScan
                        : null,
                    icon: const Icon(Icons.search),
                    label: const Text('Analyze'),
                  ),
                  const SizedBox(width: 12),
                  if (controller.status == EngineStatus.scanning)
                    OutlinedButton.icon(
                      onPressed: controller.cancelScan,
                      icon: const Icon(Icons.stop),
                      label: const Text('Cancel'),
                    ),
                  if (controller.status == EngineStatus.executing)
                    OutlinedButton.icon(
                      onPressed: () => controller.cancelExecution(),
                      icon: const Icon(Icons.stop),
                      label: const Text('Stop'),
                    ),
                  const SizedBox(width: 12),
                  if (results != null &&
                      results.isTerminal &&
                      controller.selected.isNotEmpty)
                    FilledButton.tonalIcon(
                      onPressed:
                          controller.status == EngineStatus.executing
                              ? null
                              : () => _confirmAndCommit(context),
                      icon: const Icon(Icons.archive_outlined),
                      label: Text(
                          'Quarantine ${controller.selected.length} items'),
                    ),
                  const Spacer(),
                  if (results != null)
                    Text('${results.candidates.length} items · '
                        '${formatBytes(results.estimatedBytes)} · '
                        '${results.status}'),
                ],
              ),
              const SizedBox(height: 8),
              if (controller.status == EngineStatus.scanning ||
                  controller.status == EngineStatus.executing)
                const LinearProgressIndicator(),
              const SizedBox(height: 16),
              Expanded(
                child: results == null
                    ? const _EmptyState()
                    : _CandidateList(
                        candidates: results.candidates,
                        selected: controller.selected,
                        onToggle: controller.toggleSelected,
                      ),
              ),
              if (controller.lastCommitResults != null)
                _CommitReport(items: controller.lastCommitResults!),
            ],
          ),
        );
      },
    );
  }

  Future<void> _confirmAndCommit(BuildContext context) async {
    final count = controller.selected.length;
    final bytes = controller.results?.candidates
            .where((c) => controller.selected.contains(c.id))
            .fold<int>(0, (s, c) => s + c.estimatedBytes) ??
        0;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Confirm quarantine'),
        content: Text(
            'Move $count items (${formatBytes(bytes)}) to quarantine?\n\n'
            'Files are moved to ClearForge quarantine, not deleted. '
            'You can restore them from History.'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx, false),
              child: const Text('Cancel')),
          FilledButton(
              onPressed: () => Navigator.pop(ctx, true),
              child: const Text('Quarantine')),
        ],
      ),
    );
    if (confirmed == true) await controller.commitSelected();
  }
}

class _EmptyState extends StatelessWidget {
  const _EmptyState();

  @override
  Widget build(BuildContext context) => Center(
        child: Text(
          'Run Analyze to inventory removable items.\n'
          'Scans are read-only. Cleanup moves files to quarantine — never deletes.',
          textAlign: TextAlign.center,
          style: Theme.of(context).textTheme.bodyLarge,
        ),
      );
}

class _CandidateList extends StatelessWidget {
  final List<ScanCandidate> candidates;
  final Set<String> selected;
  final void Function(String, bool) onToggle;
  const _CandidateList(
      {required this.candidates,
      required this.selected,
      required this.onToggle});

  static const _riskColors = {
    RiskLevel.low: Colors.green,
    RiskLevel.medium: Colors.orange,
    RiskLevel.high: Colors.deepOrange,
    RiskLevel.restricted: Colors.red,
  };

  @override
  Widget build(BuildContext context) {
    if (candidates.isEmpty) {
      return const Center(child: Text('No removable items found.'));
    }
    return ListView.separated(
      itemCount: candidates.length,
      separatorBuilder: (_, _) => const Divider(height: 1),
      itemBuilder: (context, i) {
        final c = candidates[i];
        return ListTile(
          dense: true,
          leading: Checkbox(
            value: selected.contains(c.id),
            onChanged: (v) => onToggle(c.id, v ?? false),
          ),
          title: Row(children: [
            Chip(
              visualDensity: VisualDensity.compact,
              backgroundColor: _riskColors[c.risk]!.withAlpha(40),
              label: Text(c.risk.name,
                  style: TextStyle(color: _riskColors[c.risk])),
            ),
            const SizedBox(width: 8),
            Expanded(
                child:
                    Text(c.displayName, overflow: TextOverflow.ellipsis)),
          ]),
          subtitle: Tooltip(
              message: c.reason, child: Text(categoryLabel(c.category))),
          trailing: Text(formatBytes(c.estimatedBytes)),
        );
      },
    );
  }
}

class _CommitReport extends StatelessWidget {
  final List<CleanupItemOutcome> items;
  const _CommitReport({required this.items});

  @override
  Widget build(BuildContext context) {
    final cleaned = items.where((i) => i.status == 'cleaned').length;
    final skipped = items.length - cleaned;
    final reclaimed =
        items.fold<int>(0, (s, i) => s + i.bytesReclaimed);
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text('Last operation',
                  style: Theme.of(context).textTheme.titleSmall),
              Text('$cleaned quarantined · $skipped skipped/failed · '
                  '${formatBytes(reclaimed)} moved to quarantine'),
              if (skipped > 0)
                ...items
                    .where((i) => i.status != 'cleaned')
                    .take(5)
                    .map((i) => Text('${i.candidateId}: '
                        '${i.errorCode ?? i.status}',
                        style: Theme.of(context).textTheme.bodySmall)),
            ],
          ),
        ),
      ),
    );
  }
}
