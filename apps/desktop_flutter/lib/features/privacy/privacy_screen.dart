import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

/// Browser privacy: read-only profile detection + category availability.
/// History/cookies are surfaced with their consequence text but unavailable
/// until browser-specific lifecycle review lands.
class PrivacyScreen extends StatelessWidget {
  final EngineController controller;
  const PrivacyScreen({super.key, required this.controller});

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
                onPressed: controller.loadPrivacy,
                icon: const Icon(Icons.privacy_tip_outlined),
                label: const Text('Detect browser profiles'),
              ),
              const SizedBox(width: 12),
              FilledButton.tonalIcon(
                onPressed: controller.startPrivacyCacheScan,
                icon: const Icon(Icons.search),
                label: const Text('Scan caches'),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                    'Cache items appear under Cleaner → review → quarantine. History/cookies are never touched.',
                    style: Theme.of(context).textTheme.bodySmall),
              ),
            ]),
            const SizedBox(height: 16),
            Expanded(
              child: controller.privacyProfiles.isEmpty
                  ? const Center(
                      child: Text('Detect profiles to preview privacy data.'))
                  : ListView(
                      children: [
                        for (final p in controller.privacyProfiles)
                          Card(
                            child: Padding(
                              padding: const EdgeInsets.all(12),
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text('${p['browser']} · ${p['profileName']}',
                                      style: Theme.of(context)
                                          .textTheme
                                          .titleSmall),
                                  Text(
                                      'cache: ${formatBytes(p['cacheBytes'] as int? ?? 0)}'
                                      '${(p['browserRunning'] == true) ? ' · browser running — cleaning disabled' : ''}'),
                                  const SizedBox(height: 8),
                                  for (final c in (p['categories']
                                          as Map<String, dynamic>? ?? {})
                                      .entries)
                                    Row(children: [
                                      Icon(
                                        (c.value['available'] == true)
                                            ? Icons.check_circle_outline
                                            : Icons.block,
                                        size: 16,
                                      ),
                                      const SizedBox(width: 6),
                                      Expanded(
                                        child: Text(
                                          '${c.key} — '
                                          '${c.value['available'] == true ? (c.value['warning'] ?? 'available') : (c.value['unavailableReason'] ?? 'unavailable')}',
                                          style: Theme.of(context)
                                              .textTheme
                                              .bodySmall,
                                        ),
                                      ),
                                    ]),
                                ],
                              ),
                            ),
                          ),
                      ]),
            ),
          ],
        ),
      ),
    );
  }
}
