import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

/// M8.1 unified Smart Scan: one coordinated, deduplicated view over all
/// scan producers. Actionable findings execute only through the existing
/// preview → approval → journaled-commit pipeline; review-only findings are
/// evidence, never selectable. Findings whose backing session expired or
/// was evicted are displayed but never selectable — they require a rescan.
class SmartScanScreen extends StatelessWidget {
  final EngineController controller;
  const SmartScanScreen({super.key, required this.controller});

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) {
        final scan = controller.smartScan;
        final running = scan?.isRunning ?? false;
        return Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(children: [
                FilledButton.icon(
                  onPressed: controller.status == EngineStatus.ready
                      ? controller.startSmartScan
                      : null,
                  icon: const Icon(Icons.manage_search),
                  label: const Text('Smart Scan'),
                ),
                const SizedBox(width: 12),
                if (running)
                  OutlinedButton.icon(
                    onPressed: controller.cancelSmartScan,
                    icon: const Icon(Icons.stop),
                    label: const Text('Cancel'),
                  ),
                if (scan != null && scan.isTerminal &&
                    controller.smartSelected.isNotEmpty)
                  FilledButton.tonalIcon(
                    onPressed: controller.smartExecuting
                        ? null
                        : () => _confirmAndCommit(context),
                    icon: const Icon(Icons.archive_outlined),
                    label: Text(
                        'Quarantine ${controller.smartSelected.length} finding(s)'),
                  ),
                const Spacer(),
                if (scan != null) Text(_summary(scan)),
              ]),
              const SizedBox(height: 8),
              if (controller.smartNeedsRescan)
                Card(
                  color: Theme.of(context).colorScheme.errorContainer,
                  child: Padding(
                    padding: const EdgeInsets.all(12),
                    child: Row(children: [
                      const Icon(Icons.schedule_outlined),
                      const SizedBox(width: 8),
                      const Expanded(
                        child: Text(
                          'Some findings can no longer execute — their scan '
                          'session expired or was evicted. Nothing was moved. '
                          'Run a fresh Smart Scan to act on current data.',
                        ),
                      ),
                      FilledButton.tonal(
                        onPressed: controller.status == EngineStatus.ready
                            ? controller.startSmartScan
                            : null,
                        child: const Text('Rescan'),
                      ),
                    ]),
                  ),
                ),
              if (running) const LinearProgressIndicator(),
              if (scan != null && scan.producers.isNotEmpty)
                _ProducerStrip(producers: scan.producers),
              const SizedBox(height: 8),
              Expanded(
                child: scan == null
                    ? const _EmptyState()
                    : _FindingsList(
                        findings: scan.findings,
                        selected: controller.smartSelected,
                        committed: controller.smartCommitted,
                        staleSessions: controller.smartStaleSessions,
                        onToggle: controller.toggleSmartSelected,
                        onDetails: (f) => _showDetails(context, f),
                      ),
              ),
              if (controller.smartCommitResults != null)
                _OutcomeReport(
                    items: controller.smartCommitResults!,
                    findings: scan?.findings ?? const []),
            ],
          ),
        );
      },
    );
  }

  String _summary(SmartScanSnapshot s) {
    final actionable = s.findings.where((f) => f.actionable).length;
    final reviewOnly = s.findings.length - actionable;
    final statusText = s.userCancelled ? 'cancelled by user' : s.status;
    return '${s.findings.length} findings · $actionable actionable · '
        '$reviewOnly review-only · ~${formatBytes(s.estimatedLogicalBytes)} '
        'est. logical (not guaranteed) · $statusText';
  }

  /// Read-only review of exactly which files a finding covers. No preview
  /// approval is recorded — this is evidence, not part of the commit path.
  Future<void> _showDetails(BuildContext context, SmartFinding f) async {
    final items = await controller.smartFindingDetails(f);
    if (!context.mounted) return;
    if (items == null) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
        content: Text('Details unavailable — the scan session is gone. '
            'Run a fresh Smart Scan.'),
      ));
      return;
    }
    await showDialog<void>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(f.title),
        content: SizedBox(
          width: 560,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('${items.length} file(s) would be quarantined '
                  '(~${formatBytes(f.estimatedLogicalBytes)} estimated '
                  'logical bytes):'),
              const SizedBox(height: 8),
              Flexible(
                child: ListView.builder(
                  shrinkWrap: true,
                  itemCount: items.length,
                  itemBuilder: (_, i) => ListTile(
                    dense: true,
                    leading: const Icon(Icons.insert_drive_file_outlined,
                        size: 18),
                    title: Text(items[i].displayName,
                        overflow: TextOverflow.ellipsis),
                    subtitle: Text(items[i].category),
                    trailing:
                        Text(formatBytes(items[i].estimatedBytes)),
                  ),
                ),
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(ctx),
              child: const Text('Close')),
        ],
      ),
    );
  }

  Future<void> _confirmAndCommit(BuildContext context) async {
    final chosen = controller.smartScan!.findings
        .where((f) => controller.smartSelected.contains(f.id))
        .toList();
    final files = chosen.fold<int>(
        0, (s, f) => s + (f.candidateIds?.length ?? 0));
    final bytes = chosen.fold<int>(0, (s, f) => s + f.estimatedLogicalBytes);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Confirm quarantine'),
        content: SizedBox(
          width: 560,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Quarantine $files file(s) across ${chosen.length} '
                  'finding(s) (~${formatBytes(bytes)} estimated logical '
                  'bytes)?'),
              const SizedBox(height: 8),
              // Scope must be reviewable: every selected finding is listed
              // with its file count — nothing hides behind a bare total.
              for (final f in chosen)
                Text('• ${f.title} — ${f.candidateIds?.length ?? 0} file(s), '
                    '~${formatBytes(f.estimatedLogicalBytes)}'),
              const SizedBox(height: 8),
              const Text(
                  'Each item is revalidated and content-verified (SHA-256) '
                  'before and after the move. Files go to quarantine — '
                  'never deleted — and can be restored from History.'),
            ],
          ),
        ),
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
    if (confirmed == true) await controller.commitSmartScan();
  }
}

class _ProducerStrip extends StatelessWidget {
  final List<SmartProducerStatus> producers;
  const _ProducerStrip({required this.producers});

  @override
  Widget build(BuildContext context) => Wrap(
        spacing: 8,
        children: producers
            .map((p) => Tooltip(
                  message: p.diagnostic ?? p.status,
                  child: Chip(
                    visualDensity: VisualDensity.compact,
                    avatar: Icon(_icon(p.status), size: 16),
                    label: Text(
                        '${p.producerId} · ${p.findings}'),
                  ),
                ))
            .toList(),
      );

  IconData _icon(String status) => switch (status) {
        'completed' => Icons.check_circle_outline,
        'running' => Icons.sync,
        'failed' => Icons.error_outline,
        'cancelled' => Icons.block,
        _ => Icons.schedule,
      };
}

class _EmptyState extends StatelessWidget {
  const _EmptyState();

  @override
  Widget build(BuildContext context) => Center(
        child: Text(
          'Run Smart Scan to see unified, deduplicated findings.\n'
          'Scanning is read-only; cleanup always goes through quarantine.',
          textAlign: TextAlign.center,
          style: Theme.of(context).textTheme.bodyLarge,
        ),
      );
}

class _FindingsList extends StatelessWidget {
  final List<SmartFinding> findings;
  final Set<String> selected;
  final Set<String> committed;
  final Set<String> staleSessions;
  final void Function(String, bool) onToggle;
  final void Function(SmartFinding) onDetails;
  const _FindingsList(
      {required this.findings,
      required this.selected,
      required this.committed,
      required this.staleSessions,
      required this.onToggle,
      required this.onDetails});

  static const _riskColors = {
    RiskLevel.low: Colors.green,
    RiskLevel.medium: Colors.orange,
    RiskLevel.high: Colors.deepOrange,
    RiskLevel.restricted: Colors.red,
  };

  bool _stale(SmartFinding f) =>
      f.stale ||
      (f.sessionId != null && staleSessions.contains(f.sessionId));

  @override
  Widget build(BuildContext context) {
    if (findings.isEmpty) {
      return const Center(child: Text('No findings.'));
    }
    final byCategory = <String, List<SmartFinding>>{};
    for (final f in findings) {
      byCategory.putIfAbsent(f.category, () => []).add(f);
    }
    final categories = byCategory.keys.toList()..sort();
    return ListView(
      children: [
        for (final cat in categories) ...[
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 8),
            child: Text(categoryLabel(cat),
                style: const TextStyle(fontWeight: FontWeight.bold)),
          ),
          for (final f in byCategory[cat]!)
            ListTile(
              dense: true,
              leading: _leading(f),
              title: Row(children: [
                Chip(
                  visualDensity: VisualDensity.compact,
                  backgroundColor: _riskColors[f.risk]!.withAlpha(40),
                  label: Text(f.risk.name,
                      style: TextStyle(color: _riskColors[f.risk])),
                ),
                const SizedBox(width: 8),
                Expanded(child: Text(f.title, overflow: TextOverflow.ellipsis)),
                if (f.actionable)
                  IconButton(
                    tooltip: 'Review the exact files this finding covers',
                    icon: const Icon(Icons.fact_check_outlined, size: 18),
                    onPressed: () => onDetails(f),
                  ),
              ]),
              subtitle: Tooltip(
                message: '${f.reason}\n${f.evidence.join('\n')}'
                    '${f.duplicateOf != null ? '\ncovered by ${f.duplicateOf}' : ''}',
                child: Text(
                    '${f.producer} · ${f.confidence} confidence'
                    '${f.skipReason != null ? ' · ${f.skipReason}' : ''}'),
              ),
              trailing: Text(formatBytes(f.estimatedLogicalBytes)),
            ),
        ],
      ],
    );
  }

  Widget _leading(SmartFinding f) {
    if (committed.contains(f.id)) {
      return const Tooltip(
          message: 'quarantined',
          child: Icon(Icons.check_circle_outline, size: 20));
    }
    if (f.actionable && _stale(f)) {
      return const Tooltip(
          message: 'session expired — rescan required',
          child: Icon(Icons.schedule_outlined, size: 20));
    }
    if (f.actionable && f.selectable) {
      return Checkbox(
        value: selected.contains(f.id),
        onChanged: (v) => onToggle(f.id, v ?? false),
      );
    }
    return const Icon(Icons.visibility_outlined, size: 20);
  }
}

class _OutcomeReport extends StatelessWidget {
  final List<CleanupItemOutcome> items;
  final List<SmartFinding> findings;
  const _OutcomeReport({required this.items, required this.findings});

  /// Map an outcome's raw session/candidate id back to the finding title
  /// the user actually reviewed — session ids are engine internals, not UI.
  String _label(String rawId) {
    for (final f in findings) {
      if (f.id == rawId) return f.title;
      if (f.sessionId == rawId) {
        return '${f.title} (session error)';
      }
      if (f.candidateIds != null && f.candidateIds!.contains(rawId)) {
        return '${f.title} — item $rawId';
      }
    }
    return rawId;
  }

  @override
  Widget build(BuildContext context) {
    final cleaned = items.where((i) => i.status == 'cleaned').length;
    final integrity = items
        .where((i) => (i.errorCode ?? '').startsWith('integrity_'))
        .length;
    final skipped = items.length - cleaned - integrity;
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text('Outcome',
                  style: Theme.of(context).textTheme.titleSmall),
              Text('$cleaned quarantined · $skipped skipped/failed · '
                  '$integrity integrity/recovery errors'),
              if (skipped + integrity > 0)
                ...items
                    .where((i) => i.status != 'cleaned')
                    .take(6)
                    .map((i) => Text('${_label(i.candidateId)}: '
                        '${i.errorCode ?? i.status}',
                        style: Theme.of(context).textTheme.bodySmall)),
              const SizedBox(height: 4),
              Text('Restores are managed in History.',
                  style: Theme.of(context).textTheme.bodySmall),
            ],
          ),
        ),
      ),
    );
  }
}
