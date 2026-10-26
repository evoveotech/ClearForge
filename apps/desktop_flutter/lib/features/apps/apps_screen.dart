import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

/// M8.4a read-only installed-application inventory (classic Uninstall hives).
/// No uninstall/reset actions exist in this milestone — management hands off
/// to Windows Settings; leftover mapping is a separate gated milestone.
class AppsScreen extends StatefulWidget {
  final EngineController controller;
  const AppsScreen({super.key, required this.controller});

  @override
  State<AppsScreen> createState() => _AppsScreenState();
}

class _AppsScreenState extends State<AppsScreen> {
  String _filter = '';
  bool _showSystem = false;

  @override
  Widget build(BuildContext context) {
    final controller = widget.controller;
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) {
        final visible = controller.apps.where((a) {
          if (!_showSystem && a['kind'] != 'app') return false;
          if (_filter.isEmpty) return true;
          final hay =
              '${a['name'] ?? ''} ${a['publisher'] ?? ''}'.toLowerCase();
          return hay.contains(_filter.toLowerCase());
        }).toList();
        return Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(children: [
                FilledButton.icon(
                  onPressed:
                      controller.appsLoading ? null : controller.loadApps,
                  icon: const Icon(Icons.apps_outlined),
                  label: Text(controller.appsLoading
                      ? 'Loading…'
                      : 'Load installed apps'),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    controller.appsNote ??
                        'Read-only — uninstall via Settings > Apps',
                    style: Theme.of(context).textTheme.bodySmall,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ]),
              const SizedBox(height: 12),
              Row(children: [
                SizedBox(
                  width: 280,
                  child: TextField(
                    decoration: const InputDecoration(
                      isDense: true,
                      prefixIcon: Icon(Icons.search, size: 18),
                      hintText: 'Filter by name or publisher',
                      border: OutlineInputBorder(),
                    ),
                    onChanged: (v) => setState(() => _filter = v),
                  ),
                ),
                const SizedBox(width: 16),
                FilterChip(
                  label: const Text('include system components & updates'),
                  selected: _showSystem,
                  onSelected: (v) => setState(() => _showSystem = v),
                ),
                const Spacer(),
                if (controller.apps.isNotEmpty)
                  Text('${visible.length} of ${controller.apps.length} apps',
                      style: Theme.of(context).textTheme.bodySmall),
              ]),
              const SizedBox(height: 12),
              Expanded(
                child: controller.apps.isEmpty
                    ? Center(
                        child: Text(controller.appsNote == null
                            ? 'Load the inventory to review installed applications.'
                            : 'No registered applications found.'))
                    : ListView.separated(
                        itemCount: visible.length,
                        separatorBuilder: (_, _) => const Divider(height: 1),
                        itemBuilder: (context, i) =>
                            _AppTile(app: visible[i]),
                      ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _AppTile extends StatelessWidget {
  final Map<String, dynamic> app;
  const _AppTile({required this.app});

  @override
  Widget build(BuildContext context) {
    final sizeBytes = app['sizeBytes'] as int?;
    final kind = app['kind'] as String? ?? 'app';
    final details = <String>[
      app['source'] as String? ?? '',
      app['scope'] as String? ?? '',
      if (kind != 'app') kind,
      if (app['hasUninstaller'] == false) 'no uninstaller registered',
    ].join(' · ');
    return ListTile(
      dense: true,
      leading: Icon(
        kind == 'app'
            ? (app['hasUninstaller'] == true
                ? Icons.verified_outlined
                : Icons.help_outline)
            : Icons.inventory_2_outlined,
      ),
      title: Text(app['name'] as String? ?? '',
          overflow: TextOverflow.ellipsis),
      subtitle: Text(
        [
          if (app['publisher'] != null) app['publisher'],
          if (app['version'] != null) 'v${app['version']}',
          if (app['installLocation'] != null) app['installLocation'],
          details,
        ].join('\n'),
        overflow: TextOverflow.ellipsis,
        maxLines: 2,
      ),
      trailing: Text(
        [
          if (sizeBytes != null) formatBytes(sizeBytes),
          if (app['installDate'] != null) app['installDate'],
        ].join('\n'),
        textAlign: TextAlign.end,
        style: Theme.of(context).textTheme.bodySmall,
      ),
    );
  }
}
