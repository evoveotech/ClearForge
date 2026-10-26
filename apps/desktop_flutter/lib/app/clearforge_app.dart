import 'package:flutter/material.dart';

import '../core/state/engine_controller.dart';
import '../features/apps/apps_screen.dart';
import '../features/cleaner/cleaner_screen.dart';
import '../features/duplicates/duplicates_screen.dart';
import '../features/history/history_screen.dart';
import '../features/models/models_screen.dart';
import '../features/privacy/privacy_screen.dart';
import '../features/smartscan/smart_scan_screen.dart';
import '../features/startup/startup_screen.dart';
import '../features/storage/storage_screen.dart';

class ClearForgeApp extends StatefulWidget {
  const ClearForgeApp({super.key, this.controller});

  /// Test injection point: production always uses the default controller;
  /// tests pass one bound to a unique test-owned pipe + strict process match.
  final EngineController? controller;

  @override
  State<ClearForgeApp> createState() => _ClearForgeAppState();
}

class _ClearForgeAppState extends State<ClearForgeApp> {
  late final EngineController _controller =
      widget.controller ?? EngineController();
  int _navIndex = 0;

  @override
  void initState() {
    super.initState();
    _controller.connect();
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'ClearForge',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(colorSchemeSeed: Colors.teal, useMaterial3: true),
      home: Scaffold(
        appBar: AppBar(
          title: const Text('ClearForge'),
          actions: [
            ListenableBuilder(
              listenable: _controller,
              builder: (context, _) => Padding(
                padding: const EdgeInsets.only(right: 16),
                child: Center(
                  child: Text(_statusLabel(_controller),
                      style: Theme.of(context).textTheme.bodySmall),
                ),
              ),
            ),
          ],
        ),
        body: _controller.status == EngineStatus.error
            ? _ErrorState(
                message: _controller.errorMessage ?? 'unknown error',
                onRetry: _controller.connect,
              )
            : Row(
                children: [
                  NavigationRail(
                    selectedIndex: _navIndex,
                    onDestinationSelected: (i) =>
                        setState(() => _navIndex = i),
                    labelType: NavigationRailLabelType.all,
                    destinations: const [
                      NavigationRailDestination(
                          icon: Icon(Icons.manage_search),
                          label: Text('Smart Scan')),
                      NavigationRailDestination(
                          icon: Icon(Icons.cleaning_services_outlined),
                          label: Text('Cleaner')),
                      NavigationRailDestination(
                          icon: Icon(Icons.insights),
                          label: Text('Storage')),
                      NavigationRailDestination(
                          icon: Icon(Icons.copy_all),
                          label: Text('Duplicates')),
                      NavigationRailDestination(
                          icon: Icon(Icons.rocket_launch_outlined),
                          label: Text('Startup')),
                      NavigationRailDestination(
                          icon: Icon(Icons.apps_outlined),
                          label: Text('Apps')),
                      NavigationRailDestination(
                          icon: Icon(Icons.privacy_tip_outlined),
                          label: Text('Privacy')),
                      NavigationRailDestination(
                          icon: Icon(Icons.psychology_outlined),
                          label: Text('AI Models')),
                      NavigationRailDestination(
                          icon: Icon(Icons.history),
                          label: Text('History')),
                    ],
                  ),
                  const VerticalDivider(width: 1),
                  Expanded(
                    child: switch (_navIndex) {
                      0 => SmartScanScreen(controller: _controller),
                      1 => CleanerScreen(controller: _controller),
                      2 => StorageScreen(controller: _controller),
                      3 => DuplicatesScreen(controller: _controller),
                      4 => StartupScreen(controller: _controller),
                      5 => AppsScreen(controller: _controller),
                      6 => PrivacyScreen(controller: _controller),
                      7 => ModelsScreen(controller: _controller),
                      _ => HistoryScreen(controller: _controller),
                    },
                  ),
                ],
              ),
      ),
    );
  }

  String _statusLabel(EngineController c) => switch (c.status) {
        EngineStatus.disconnected => 'engine: disconnected',
        EngineStatus.starting => 'engine: starting…',
        EngineStatus.ready => 'engine ${c.engineVersion ?? ''} · ready',
        EngineStatus.scanning => 'engine: scanning…',
        EngineStatus.executing => 'engine: cleaning…',
        EngineStatus.error => 'engine: error',
      };
}

class _ErrorState extends StatelessWidget {
  final String message;
  final VoidCallback onRetry;
  const _ErrorState({required this.message, required this.onRetry});

  @override
  Widget build(BuildContext context) => Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.error_outline, size: 48),
            const SizedBox(height: 12),
            Text(message, textAlign: TextAlign.center),
            const SizedBox(height: 12),
            FilledButton(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      );
}
