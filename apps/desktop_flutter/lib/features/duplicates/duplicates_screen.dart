import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';
import 'duplicate_group_review.dart';

/// Duplicate finder (M8.3): a list of verified byte-identical groups. Each
/// group opens the keeper-review dialog — the user inspects every member,
/// explicitly chooses exactly one copy to keep, checks the copies to
/// quarantine, and executes through the keeper-bound preview → approval →
/// commit pipeline. Nothing is auto-selected and no copy moves without
/// explicit approval recorded engine-side.
class DuplicatesScreen extends StatefulWidget {
  final EngineController controller;
  const DuplicatesScreen({super.key, required this.controller});

  @override
  State<DuplicatesScreen> createState() => _DuplicatesScreenState();
}

class _DuplicatesScreenState extends State<DuplicatesScreen> {
  EngineController get controller => widget.controller;

  /// Groups the user fully resolved this session — kept visible as done.
  final Set<String> _resolved = {};

  Future<void> _openReview(Map<String, dynamic> g) async {
    final sessionId = controller.duplicateSessionId ?? '';
    final groupId = g['groupId'] as String? ?? '';
    final done = await DuplicateGroupReviewDialog.show(context,
        controller: controller,
        sessionId: sessionId,
        groupId: groupId,
        onFullyResolved: () => _resolved.add(groupId));
    if (done == true && mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) => Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              FilledButton.icon(
                onPressed: controller.status == EngineStatus.ready
                    ? () {
                        _resolved.clear();
                        controller.startDuplicatesScan(
                            const ['downloads', 'documents', 'desktop']);
                      }
                    : null,
                icon: const Icon(Icons.copy_all),
                label: const Text('Find duplicates'),
              ),
              const SizedBox(width: 12),
              Text('Scopes: Downloads, Documents, Desktop',
                  style: Theme.of(context).textTheme.bodySmall),
            ]),
            if (controller.status == EngineStatus.scanning)
              const Padding(
                  padding: EdgeInsets.symmetric(vertical: 8),
                  child: LinearProgressIndicator()),
            const SizedBox(height: 16),
            Expanded(
              child: controller.duplicateGroups.isEmpty
                  ? const Center(
                      child: Text('No duplicate groups yet. Run a scan.'))
                  : ListView.builder(
                      itemCount: controller.duplicateGroups.length,
                      itemBuilder: (context, i) {
                        final g = controller.duplicateGroups[i];
                        final copies = g['copies'] as int? ?? 0;
                        final bytes = g['fileBytes'] as int? ?? 0;
                        final resolved =
                            _resolved.contains(g['groupId']);
                        return Card(
                          child: ListTile(
                            leading: Icon(resolved
                                ? Icons.check_circle_outline
                                : Icons.copy_all),
                            title: Text(
                                'Group ${g['groupId']} · $copies identical copies · ${formatBytes(bytes)} each'),
                            subtitle: Text(
                                'sha256:${g['sha256Short']}… · up to '
                                '${formatBytes(bytes * (copies - 1))} '
                                'reclaimable logical bytes '
                                '${resolved ? '· resolved' : ''}'),
                            trailing: FilledButton.tonalIcon(
                              onPressed: () => _openReview(g),
                              icon: const Icon(Icons.fact_check_outlined),
                              label: const Text('Review & choose keeper'),
                            ),
                          ),
                        );
                      }),
            ),
          ],
        ),
      ),
    );
  }
}
