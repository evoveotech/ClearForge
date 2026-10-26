import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

/// Duplicate finder: group review with an explicit keep choice and an
/// explicit selection of copies to quarantine. Nothing is auto-selected and
/// no copy moves without a confirmation — the engine additionally requires
/// a preview approval for the exact ids committed.
class DuplicatesScreen extends StatefulWidget {
  final EngineController controller;
  const DuplicatesScreen({super.key, required this.controller});

  @override
  State<DuplicatesScreen> createState() => _DuplicatesScreenState();
}

class _DuplicatesScreenState extends State<DuplicatesScreen> {
  EngineController get controller => widget.controller;

  /// Per-group review state: the copy index to keep, and the copy indexes
  /// the user explicitly chose to quarantine.
  final Map<String, int> _keep = {};
  final Map<String, Set<int>> _act = {};

  String _groupKey(Map<String, dynamic> g) => g['groupId'] as String? ?? '';

  List<String> _candidateIds(Map<String, dynamic> g) =>
      (g['candidateIds'] as List<dynamic>? ?? []).cast<String>();

  int _keepIndex(String key) => _keep[key] ?? 0;
  Set<int> _actSet(String key) => _act.putIfAbsent(key, () => <int>{});

  void _setKeep(String key, int index) => setState(() {
        _keep[key] = index;
        _actSet(key).remove(index); // the kept copy can never be selected
      });

  void _toggleAct(String key, int index, bool value) => setState(() {
        value ? _actSet(key).add(index) : _actSet(key).remove(index);
      });

  Future<void> _confirmAndCommit(
      BuildContext context, Map<String, dynamic> g) async {
    final key = _groupKey(g);
    final ids = _candidateIds(g);
    final selected = _actSet(key).toList()..sort();
    if (selected.isEmpty) return;
    final sessionId = g['sessionId'] as String? ?? '';
    final bytes = (g['fileBytes'] as int? ?? 0) * selected.length;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Confirm quarantine'),
        content: Text(
            'Quarantine ${selected.length} duplicate '
            'cop${selected.length == 1 ? 'y' : 'ies'} '
            '(${formatBytes(bytes)})? Copy #${_keepIndex(key) + 1} is kept.\n\n'
            'Files are moved to quarantine, not deleted. '
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
    if (confirmed != true) return;
    final chosen = [for (final k in selected) ids[k]];
    final ok = await controller.commitDuplicateCandidates(sessionId, chosen);
    if (ok && mounted) {
      setState(() {
        // The committed copies are gone; drop them from the review state.
        _actSet(key).clear();
      });
    }
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
                    ? () => controller.startDuplicatesScan(
                        const ['downloads', 'documents', 'desktop'])
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
                        final key = _groupKey(g);
                        final ids = _candidateIds(g);
                        final keep = _keepIndex(key);
                        final act = _actSet(key);
                        return ExpansionTile(
                          title: Text(
                              'Group ${g['groupId']} · $copies copies · ${formatBytes(bytes)} each'),
                          subtitle: Text(
                              'sha256:${g['sha256Short']}… · recoverable ${formatBytes(bytes * (copies - 1))}'),
                          children: [
                            RadioGroup<int>(
                              groupValue: keep,
                              onChanged: (v) => _setKeep(key, v ?? 0),
                              child: Column(
                                children: [
                                  for (var k = 0; k < ids.length; k++)
                                    ListTile(
                                      dense: true,
                                      leading: Radio<int>(value: k),
                                      title: Text(ids[k],
                                          overflow: TextOverflow.ellipsis),
                                      subtitle: Text(k == keep
                                          ? 'keep this copy'
                                          : 'copy $k'),
                                      trailing: k == keep
                                          ? null
                                          : Checkbox(
                                              value: act.contains(k),
                                              onChanged: (v) => _toggleAct(
                                                  key, k, v ?? false),
                                            ),
                                    ),
                                ],
                              ),
                            ),
                            Padding(
                              padding:
                                  const EdgeInsets.symmetric(horizontal: 16),
                              child: Align(
                                alignment: Alignment.centerRight,
                                child: FilledButton.tonalIcon(
                                  onPressed: act.isEmpty
                                      ? null
                                      : () => _confirmAndCommit(context, g),
                                  icon: const Icon(Icons.archive_outlined),
                                  label: Text(
                                      'Quarantine ${act.length} selected'),
                                ),
                              ),
                            ),
                            const SizedBox(height: 8),
                          ],
                        );
                      }),
            ),
          ],
        ),
      ),
    );
  }
}
