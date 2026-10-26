import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

/// Quarantine + operation history with restore. Restores are journaled ops;
/// the engine refuses overwrites and ambiguous states.
class HistoryScreen extends StatelessWidget {
  final EngineController controller;
  const HistoryScreen({super.key, required this.controller});

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) {
        final entries = controller.history;
        return Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(children: [
                Text('History & quarantine',
                    style: Theme.of(context).textTheme.titleLarge),
                const Spacer(),
                OutlinedButton.icon(
                  onPressed: controller.refreshHistory,
                  icon: const Icon(Icons.refresh),
                  label: const Text('Refresh'),
                ),
              ]),
              const SizedBox(height: 16),
              Expanded(
                child: entries.isEmpty
                    ? const Center(child: Text('No operations yet.'))
                    : ListView.separated(
                        itemCount: entries.length,
                        separatorBuilder: (_, _) => const Divider(height: 1),
                        itemBuilder: (context, i) {
                          final e = entries[i];
                          final canRestore = e.state == 'quarantined';
                          return ListTile(
                            dense: true,
                            leading: Icon(_icon(e.state)),
                            title: Text(e.displayName ?? e.candidateId,
                                overflow: TextOverflow.ellipsis),
                            subtitle: Text(
                                '${e.category ?? '?'} · ${e.state}'
                                '${_integrityLabel(e.integrity)}'
                                '${e.errorCode != null ? ' · ${e.errorCode}' : ''}'),
                            trailing: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Text(formatBytes(e.bytes)),
                                if (canRestore) ...[
                                  const SizedBox(width: 8),
                                  TextButton(
                                    onPressed: () =>
                                        controller.restore([e.operationId]),
                                    child: const Text('Restore'),
                                  ),
                                ],
                              ],
                            ),
                          );
                        }),
              ),
            ],
          ),
        );
      },
    );
  }

  /// Honest integrity marker: 'verified'/'restored' mean the content was
  /// cryptographically confirmed; 'legacy*' means the record predates hashing
  /// and is explicitly unverified — never rendered as a check mark.
  String _integrityLabel(String? integrity) => switch (integrity) {
        'verified' || 'restored' => ' · verified',
        'legacy' => ' · legacy (unverified)',
        'legacy_restored' => ' · restored (unverified)',
        'failed' => ' · integrity failed',
        'captured' => ' · verifying',
        _ => '',
      };

  IconData _icon(String state) => switch (state) {
        'quarantined' => Icons.archive_outlined,
        'restored' => Icons.undo,
        'failed' => Icons.error_outline,
        'recoveryrequired' => Icons.warning_amber,
        _ => Icons.schedule,
      };
}
