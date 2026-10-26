import 'package:flutter/material.dart';

import '../../core/state/engine_controller.dart';

/// Read-only startup inventory. Modifications hand off to Windows Settings —
/// direct controls land as a separately tested feature, not this milestone.
class StartupScreen extends StatelessWidget {
  final EngineController controller;
  const StartupScreen({super.key, required this.controller});

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
                onPressed: controller.loadStartup,
                icon: const Icon(Icons.playlist_play),
                label: const Text('Load startup entries'),
              ),
              const SizedBox(width: 12),
              Text('Read-only — changes via Settings > Apps > Startup',
                  style: Theme.of(context).textTheme.bodySmall),
            ]),
            const SizedBox(height: 16),
            Expanded(
              child: controller.startupEntries.isEmpty
                  ? const Center(
                      child: Text('Load the inventory to review startup items.'))
                  : ListView.separated(
                      itemCount: controller.startupEntries.length,
                      separatorBuilder: (_, _) => const Divider(height: 1),
                      itemBuilder: (context, i) {
                        final e = controller.startupEntries[i];
                        return ListTile(
                          dense: true,
                          leading: Icon(
                            (e['confidence'] == 'verified')
                                ? Icons.verified_outlined
                                : Icons.help_outline,
                          ),
                          title: Text(e['name'] as String? ?? '',
                              overflow: TextOverflow.ellipsis),
                          subtitle: Text(
                              '${e['source']} · ${e['publisher'] ?? 'unknown publisher'}',
                              overflow: TextOverflow.ellipsis),
                          trailing: Text(e['status'] as String? ?? ''),
                        );
                      }),
            ),
          ],
        ),
      ),
    );
  }
}
