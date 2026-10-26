import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';

/// M8.3 duplicate-group review dialog.
///
/// Loads per-member detail via `duplicates.group`, lets the user explicitly
/// choose exactly ONE keeper (the engine's newest-member pick is only a
/// labelled suggestion), then quarantines exactly the checked copies through
/// the keeper-bound preview → approval → commit pipeline. Shows honest
/// empty/loading/stale/error/success states and never claims success before
/// the engine reports per-item outcomes.
class DuplicateGroupReviewDialog extends StatefulWidget {
  final EngineController controller;
  final String sessionId;
  final String groupId;

  /// Called after a commit where every selected copy reached 'cleaned' —
  /// callers use it to mark their finding/group resolved.
  final VoidCallback? onFullyResolved;

  const DuplicateGroupReviewDialog({
    super.key,
    required this.controller,
    required this.sessionId,
    required this.groupId,
    this.onFullyResolved,
  });

  /// Show the review dialog; resolves true when the commit fully succeeded.
  static Future<bool?> show(BuildContext context,
      {required EngineController controller,
      required String sessionId,
      required String groupId,
      VoidCallback? onFullyResolved}) {
    return showDialog<bool>(
      context: context,
      builder: (_) => DuplicateGroupReviewDialog(
          controller: controller,
          sessionId: sessionId,
          groupId: groupId,
          onFullyResolved: onFullyResolved),
    );
  }

  @override
  State<DuplicateGroupReviewDialog> createState() =>
      _DuplicateGroupReviewDialogState();
}

class _DuplicateGroupReviewDialogState
    extends State<DuplicateGroupReviewDialog> {
  Map<String, dynamic>? _group;
  bool _loading = true;
  String? _error;

  /// Explicit keeper choice — null until the user picks (never pre-decided).
  String? _keeperId;

  /// Explicit quarantine selection — empty until the user checks members.
  final Set<String> _selected = {};

  bool _busy = false;
  List<CleanupItemOutcome>? _outcomes;
  String? _commitError;

  List<Map<String, dynamic>> get _members =>
      (_group?['members'] as List<dynamic>? ?? [])
          .cast<Map<String, dynamic>>();

  String? get _suggested => _group?['suggestedKeeperId'] as String?;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final g = await widget.controller
        .duplicateGroupDetail(widget.sessionId, widget.groupId);
    if (!mounted) return;
    setState(() {
      _loading = false;
      if (g == null) {
        _error = 'This duplicate group is no longer available — its scan '
            'session expired or was cancelled. Run a fresh scan.';
      } else {
        _group = g;
      }
    });
  }

  void _pickKeeper(String id) => setState(() {
        _keeperId = id;
        _selected.remove(id); // the keeper can never be quarantined
      });

  void _toggle(String id, bool v) => setState(() {
        v ? _selected.add(id) : _selected.remove(id);
      });

  Future<void> _commit() async {
    final keeper = _keeperId;
    if (keeper == null || _selected.isEmpty) return;
    setState(() {
      _busy = true;
      _commitError = null;
    });
    final err = await widget.controller.commitDuplicateGroup(
        widget.sessionId, widget.groupId, keeper, _selected.toList());
    if (!mounted) return;
    setState(() {
      _busy = false;
      _commitError = err;
      _outcomes = widget.controller.lastCommitResults;
    });
    if (err == null &&
        _outcomes != null &&
        _outcomes!.every((o) => o.status == 'cleaned' ||
            o.status == 'already_quarantined')) {
      widget.onFullyResolved?.call();
      if (mounted) Navigator.of(context).pop(true);
    }
  }

  static String _eligibilityText(String? e) => switch (e) {
        'eligible' => 'available',
        'missing' => 'no longer exists at this path',
        'changed' => 'changed since the scan — re-review required',
        'locked' => 'in use by another program — cannot be moved',
        'reparse_point' => 'link/junction — never followed or moved',
        'elevation_required' => 'requires elevation — out of scope',
        'unreadable' => 'cannot be read',
        _ => 'unknown',
      };

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text('Review duplicate group ${widget.groupId}'),
      content: SizedBox(
        width: 640,
        child: _loading
            ? const Padding(
                padding: EdgeInsets.all(32),
                child: Center(child: CircularProgressIndicator()))
            : _error != null
                ? _StaleOrError(message: _error!)
                : _buildReview(context),
      ),
      actions: [
        TextButton(
            onPressed: _busy ? null : () => Navigator.of(context).pop(false),
            child: const Text('Close')),
        if (_group != null)
          FilledButton.icon(
            onPressed: _busy ||
                    _keeperId == null ||
                    _selected.isEmpty ||
                    _group!['actionable'] != true ||
                    _group!['sessionState'] != 'live'
                ? null
                : _commit,
            icon: const Icon(Icons.archive_outlined),
            label: Text(_selected.isEmpty
                ? 'Select copies to quarantine'
                : 'Quarantine ${_selected.length} selected'),
          ),
      ],
    );
  }

  Widget _buildReview(BuildContext context) {
    final members = _members;
    final fileBytes = _group!['fileBytes'] as int? ?? 0;
    final reclaimable =
        _group!['estimatedReclaimableLogicalBytes'] as int? ?? 0;
    final sessionState = _group!['sessionState'] as String? ?? 'unknown';
    final actionable = _group!['actionable'] as bool? ?? false;

    if (members.isEmpty) {
      return const Text('No members remain in this group.');
    }

    return SingleChildScrollView(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (sessionState != 'live')
            _StaleOrError(
                message: 'Session state: $sessionState — this group is '
                    'review-only. Run a fresh scan to act on it.')
          else if (!actionable)
            const _StaleOrError(
                message: 'No member of this group can currently be '
                    'quarantined (missing, locked or changed) — review-only.'),
          SelectableText(
            'sha256:${_group!['sha256']}',
            style: Theme.of(context)
                .textTheme
                .bodySmall
                ?.copyWith(fontFamily: 'Consolas'),
          ),
          Text(
              '${members.length} byte-identical copies · '
              '${formatBytes(fileBytes)} each · up to '
              '${formatBytes(reclaimable)} estimated reclaimable logical '
              'bytes (not guaranteed free space)',
              style: Theme.of(context).textTheme.bodySmall),
          const SizedBox(height: 8),
          Text('Choose exactly one copy to keep — it is never moved:',
              style: Theme.of(context).textTheme.titleSmall),
          RadioGroup<String>(
            groupValue: _keeperId,
            onChanged: (v) { if (v != null) _pickKeeper(v); },
            child: Column(children: [
              for (final m in members) _memberTile(m),
            ]),
          ),
          const Divider(),
          if (_keeperId != null) _summary(members),
          if (_commitError != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text('Commit failed: $_commitError — review and retry; '
                  'nothing outside the approved set was touched.',
                  style: TextStyle(
                      color: Theme.of(context).colorScheme.error)),
            ),
          if (_outcomes != null)
            ..._outcomes!
                .where((o) => o.status != 'cleaned')
                .map((o) => Text('${o.candidateId}: '
                    '${o.errorCode ?? o.status}',
                    style: Theme.of(context).textTheme.bodySmall)),
          const SizedBox(height: 4),
          Text(
              'Quarantine moves files into app-managed storage — never '
              'deletes — and can be restored byte-identical from History.',
              style: Theme.of(context).textTheme.bodySmall),
        ],
      ),
    );
  }

  Widget _memberTile(Map<String, dynamic> m) {
    final id = m['candidateId'] as String? ?? '';
    final isKeeper = id == _keeperId;
    final isSuggested = id == _suggested;
    final eligible = m['eligibility'] == 'eligible';
    final selectable = _keeperId != null && !isKeeper && eligible;
    return ListTile(
      dense: true,
      contentPadding: EdgeInsets.zero,
      leading: Radio<String>(value: id),
      title: Row(children: [
        Flexible(
          child: Text(m['fileName'] as String? ?? '',
              style: const TextStyle(fontWeight: FontWeight.bold),
              overflow: TextOverflow.ellipsis),
        ),
        if (isKeeper) ...[
          const SizedBox(width: 6),
          const Chip(
              visualDensity: VisualDensity.compact,
              label: Text('KEEP')),
        ] else if (isSuggested) ...[
          const SizedBox(width: 6),
          const Chip(
              visualDensity: VisualDensity.compact,
              label: Text('suggested keeper')),
        ],
        if (_selected.contains(id)) ...[
          const SizedBox(width: 6),
          const Chip(
              visualDensity: VisualDensity.compact,
              label: Text('QUARANTINE')),
        ],
      ]),
      subtitle: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Full path stays inspectable and copyable — never substituted
          // with a truncated display path.
          Row(children: [
            Expanded(
              child: SelectableText(m['path'] as String? ?? '',
                  style: Theme.of(context)
                      .textTheme
                      .bodySmall
                      ?.copyWith(fontFamily: 'Consolas')),
            ),
            IconButton(
              tooltip: 'Copy full path',
              icon: const Icon(Icons.copy, size: 14),
              onPressed: () => Clipboard.setData(
                  ClipboardData(text: m['path'] as String? ?? '')),
            ),
          ]),
          Text('${formatBytes(m['bytes'] as int? ?? 0)} · modified '
              '${m['lastWriteUtc'] as String? ?? 'unknown'} · '
              '${_eligibilityText(m['eligibility'] as String?)}',
              style: Theme.of(context).textTheme.bodySmall),
        ],
      ),
      trailing: isKeeper
          ? null
          : Checkbox(
              value: _selected.contains(id),
              onChanged: selectable ? (v) => _toggle(id, v ?? false) : null,
            ),
      onTap: () => _pickKeeper(id),
    );
  }

  Widget _summary(List<Map<String, dynamic>> members) {
    final keeper = members
        .firstWhere((m) => m['candidateId'] == _keeperId, orElse: () => {});
    final excluded = members
        .where((m) =>
            m['candidateId'] != _keeperId &&
            !_selected.contains(m['candidateId']))
        .toList();
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('KEEP: ${keeper['path'] ?? _keeperId}',
              style: const TextStyle(fontWeight: FontWeight.bold)),
          Text('QUARANTINE: ${_selected.length} cop'
              '${_selected.length == 1 ? 'y' : 'ies'} — exactly the checked '
              'copies, nothing more'),
          if (excluded.isNotEmpty)
            Text('Left in place (not selected): ${excluded.length}'),
        ],
      ),
    );
  }
}

class _StaleOrError extends StatelessWidget {
  final String message;
  const _StaleOrError({required this.message});

  @override
  Widget build(BuildContext context) => Card(
        color: Theme.of(context).colorScheme.errorContainer,
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Row(children: [
            const Icon(Icons.warning_amber_outlined),
            const SizedBox(width: 8),
            Expanded(child: Text(message)),
          ]),
        ),
      );
}
