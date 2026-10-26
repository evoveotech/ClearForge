import 'dart:async';

import 'package:flutter/material.dart';

import '../../core/models/scan_models.dart';
import '../../core/state/engine_controller.dart';
import '../../core/state/plan_guard.dart';

/// Smart Storage: read-only inventory + evidence-backed recommendations +
/// goal planner. Estimates are labelled as estimates; review-only items
/// never offer execution.
class StorageScreen extends StatefulWidget {
  final EngineController controller;
  const StorageScreen({super.key, required this.controller});

  @override
  State<StorageScreen> createState() => _StorageScreenState();
}

class _StorageScreenState extends State<StorageScreen> {
  final _goalCtrl = TextEditingController(text: '10');

  @override
  void dispose() {
    _goalCtrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.controller;
    return ListenableBuilder(
      listenable: c,
      builder: (context, _) {
        final inv = c.storageInventory;
        return Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Wrap(spacing: 16, runSpacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
                FilledButton.icon(
                  onPressed: c.storageScanning ? null : c.loadStorageIntelligence,
                  icon: const Icon(Icons.insights),
                  label: Text(c.storageScanning ? 'Scanning…' : 'Scan storage'),
                ),
                if (inv != null)
                  Row(mainAxisSize: MainAxisSize.min, children: [
                    Text('Observed: ${formatBytes(inv['totalLogicalBytes'] as int? ?? 0)} (logical)'),
                    if (inv['partial'] == true)
                      const Padding(
                        padding: EdgeInsets.only(left: 8),
                        child: Tooltip(
                          message: 'Scan budget reached — results are incomplete',
                          child: Chip(
                            avatar: Icon(Icons.warning_amber, size: 16),
                            label: Text('partial'),
                          ),
                        ),
                      ),
                  ]),
                SizedBox(
                  width: 220,
                  child: TextField(
                    controller: _goalCtrl,
                    keyboardType: TextInputType.number,
                    decoration: InputDecoration(
                      labelText: 'Free space goal (GB)',
                      isDense: true,
                      suffixIcon: IconButton(
                        icon: const Icon(Icons.play_arrow),
                        onPressed: inv == null || c.storageScanning
                            ? null
                            : () {
                                final gb = double.tryParse(_goalCtrl.text);
                                if (gb != null && gb > 0) c.planStorageGoal(gb);
                              },
                      ),
                    ),
                  ),
                ),
              ]),
              if (c.aiAssistAvailable)
                _AiGoalAssist(controller: c)
              else
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Text(
                      'No AI model installed — open AI Models to install the '
                      'curated local model for goal assistance.',
                      style: Theme.of(context).textTheme.bodySmall),
                ),
              if (c.errorMessage != null)
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Text('Error: ${c.errorMessage}',
                      style: TextStyle(color: Theme.of(context).colorScheme.error)),
                ),
              const SizedBox(height: 12),
              if (c.storagePlanResult != null)
                _PlanBanner(
                    plan: c.storagePlanResult!,
                    controller: c,
                    goalGb: double.tryParse(_goalCtrl.text) ?? 0),
              if (c.planExecutionOutcomes != null)
                _PlanOutcome(controller: c),
              if (c.lastCommitResults != null)
                _StorageOutcome(controller: c),
              Expanded(
                child: inv == null
                    ? const Center(
                        child: Text(
                            'Run a storage scan to see where space is used\nand evidence-backed recommendations.',
                            textAlign: TextAlign.center))
                    : Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Expanded(child: _Consumers(inv: inv)),
                          const SizedBox(width: 16),
                          Expanded(
                              flex: 2,
                              child: _Recommendations(
                                  recs: c.recommendations, controller: c)),
                        ],
                      ),
              ),
            ],
          ),
        );
      },
    );
  }
}

/// Local-AI goal interpretation: a natural-language goal is turned into a
/// validated intent (goalBytes + supported constraints + clarifying
/// questions) by the isolated worker, then handed to the same deterministic
/// planner. Shown only when the curated model is installed.
class _AiGoalAssist extends StatefulWidget {
  final EngineController controller;
  const _AiGoalAssist({required this.controller});

  @override
  State<_AiGoalAssist> createState() => _AiGoalAssistState();
}

class _AiGoalAssistState extends State<_AiGoalAssist> {
  final _text = TextEditingController();
  bool _busy = false;
  Map<String, dynamic>? _intent;

  Future<void> _interpret() async {
    final c = widget.controller;
    final goal = _text.text.trim();
    if (goal.isEmpty) return;
    setState(() { _busy = true; _intent = null; });
    final recs = c.recommendations
        .map((r) => <String, dynamic>{
              'id': r.id,
              'title': r.title,
              'category': r.category,
              'estimatedLogicalBytes': r.estimatedLogicalBytes,
              'confidence': r.confidence,
            })
        .toList();
    final intent = await c.interpretGoal(goal, recs);
    if (!mounted) return;
    setState(() { _busy = false; _intent = intent; });
    if (intent == null) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(
          content: Text('AI unavailable: ${c.ai.lastError ?? "no result"}')));
      return;
    }
    // Ambiguous/unsupported requests stop at clarification — never plan a
    // guessed target on the user's behalf.
    if (intent['status'] == 'clarification_required') return;
    // Feed the validated intent to the deterministic planner — constraints
    // arrive from the engine-validated schema, never free-form text.
    final goalBytes = intent['goalBytes'] as int? ?? 0;
    final constraints = intent['constraints'] as Map<String, dynamic>? ?? {};
    await c.planStorageGoal(goalBytes / (1024 * 1024 * 1024),
        excludeIds: (constraints['excludeIds'] as List?)?.cast<String>(),
        excludeCategories:
            (constraints['excludeCategories'] as List?)?.cast<String>(),
        excludeScopes:
            (constraints['excludeScopes'] as List?)?.cast<String>());
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.controller;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      const SizedBox(height: 8),
      SizedBox(
        width: 420,
        child: TextField(
          controller: _text,
          decoration: InputDecoration(
            labelText: 'Describe your goal (local AI, offline)',
            isDense: true,
            suffixIcon: _busy
                ? const Padding(
                    padding: EdgeInsets.all(8),
                    child: SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2)))
                : IconButton(
                    icon: const Icon(Icons.psychology_outlined),
                    tooltip: 'Interpret with local AI',
                    onPressed: _interpret),
          ),
          onSubmitted: (_) => _interpret(),
        ),
      ),
      if (_intent != null) ...[
        Text(_intentSummary(_intent!),
            style: Theme.of(context).textTheme.bodySmall),
        for (final i in (_intent!['issues'] as List? ?? []))
          Text('· $i',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context).colorScheme.tertiary)),
        for (final q in (_intent!['clarifyingQuestions'] as List? ?? []))
          Text('? $q', style: Theme.of(context).textTheme.bodySmall),
      ],
      if (c.ai.lastError != null && _intent == null)
        Text('AI: ${c.ai.lastError}',
            style: Theme.of(context).textTheme.bodySmall),
    ]);
  }

  /// Plain-language summary of what the planner will receive — the
  /// interpreted target, its provenance (explicit vs model-inferred), and
  /// any exclusions. Nothing executes until the user approves the plan.
  String _intentSummary(Map<String, dynamic> intent) {
    if (intent['status'] == 'clarification_required') {
      return 'Clarification needed — no plan was generated.';
    }
    final bytes = intent['goalBytes'] as int? ?? 0;
    final source = intent['goalBytesSource'] == 'explicit_text'
        ? 'from your request'
        : 'inferred by the model — review carefully';
    final cons = intent['constraints'] as Map<String, dynamic>? ?? {};
    final parts = <String>[
      for (final e in cons.entries)
        if ((e.value as List?)?.isNotEmpty ?? false)
          '${e.key}: ${(e.value as List).join(', ')}',
    ];
    final consText = parts.isEmpty ? '' : ' · exclusions: ${parts.join('; ')}';
    return 'Interpreted target: ~${formatBytes(bytes)} ($source)$consText'
        '\n${intent['explanation']}';
  }
}

class _PlanBanner extends StatelessWidget {
  final Map<String, dynamic> plan;
  final EngineController controller;
  final double goalGb;
  const _PlanBanner({required this.plan, required this.controller,
      required this.goalGb});

  @override
  Widget build(BuildContext context) {
    final achievable = plan['likelyAchievable'] == true;
    final est = plan['estimatedTotalBytes'] as int? ?? 0;
    final missing = (plan['missingEvidence'] as List<dynamic>? ?? []);
    final selected = (plan['selected'] as List<dynamic>? ?? []);
    return Card(
      color: achievable ? Colors.green.withAlpha(25) : Colors.orange.withAlpha(25),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(
            achievable
                ? 'Goal likely achievable — ~${formatBytes(est)} from ${selected.length} eligible actions'
                : 'Goal not currently supported by evidence — ~${formatBytes(est)} safely estimated',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          for (final m in missing)
            Text('$m', style: Theme.of(context).textTheme.bodySmall),
          if (selected.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: FilledButton.tonalIcon(
                icon: const Icon(Icons.fact_check_outlined),
                label: const Text('Review & approve plan'),
                onPressed: () => showDialog<void>(
                  context: context,
                  builder: (_) => _PlanReviewSheet(
                      plan: plan, controller: controller, goalGb: goalGb),
                ),
              ),
            ),
        ]),
      ),
    );
  }
}

/// Plan review: ordered proposed actions with evidence; per-action rejection
/// re-plans server-side (never silently expanding scope). Approval executes
/// only the remaining actions through the existing preview/commit pipeline.
class _PlanReviewSheet extends StatefulWidget {
  final Map<String, dynamic> plan;
  final EngineController controller;
  final double goalGb;
  const _PlanReviewSheet({required this.plan, required this.controller,
      required this.goalGb});

  @override
  State<_PlanReviewSheet> createState() => _PlanReviewSheetState();
}

class _PlanReviewSheetState extends State<_PlanReviewSheet> {
  final Set<String> _rejected = {};
  final Map<String, Map<String, dynamic>> _previews = {};
  bool _busy = false;

  List<StorageRecommendation> _selected(Map<String, dynamic> plan) =>
      ((plan['selected'] as List<dynamic>?) ?? [])
          .map((e) =>
              StorageRecommendation.fromJson(e as Map<String, dynamic>))
          .where((r) => r.executable && !_rejected.contains(r.id))
          .toList();

  List<Map<String, dynamic>> _selectedRaw(Map<String, dynamic> plan) =>
      ((plan['selected'] as List<dynamic>?) ?? [])
          .cast<Map<String, dynamic>>()
          .where((e) => !_rejected.contains(e['id']))
          .toList();

  @override
  void initState() {
    super.initState();
    _loadPreviews();
  }

  /// Fetch engine-side previews for every proposed action so the dialog can
  /// show exact candidate counts, bytes, and expected skips before approval.
  /// Sessions above [_previewProbeCap] skip the per-item probe — its id list
  /// would exceed the 1 MiB request frame and tear down the pipe. The action
  /// card still shows its engine-side candidate count/bytes; commits chunk
  /// under the cap regardless.
  static const int _previewProbeCap = 4000;

  Future<void> _loadPreviews() async {
    final plan = widget.controller.storagePlanResult ?? widget.plan;
    for (final r in _selected(plan)) {
      final action = r.action;
      if (action == null) continue;
      if (action.candidateCount > _previewProbeCap) {
        if (mounted) {
          setState(() => _previews[r.id] = {
            'candidateCount': action.candidateCount,
            'estimatedBytes': action.eligibleBytes,
            'probeSkipped': true,
          });
        }
        continue;
      }
      final results =
          await widget.controller.storageActionCandidates(action);
      if (results == null) continue;
      final ids = results.candidates.map((c) => c.id).toList();
      final preview = await widget.controller
          .previewStorageActionDetailed(action.sessionId, ids);
      if (preview != null && mounted) {
        setState(() => _previews[r.id] = preview);
      }
    }
  }

  Future<void> _replan() async {
    setState(() => _busy = true);
    await widget.controller
        .planStorageGoal(widget.goalGb, excludeIds: _rejected.toList());
    if (mounted) setState(() => _busy = false);
    unawaited(_loadPreviews());
  }

  /// Approval guardrail: re-plan with the identical constraints and require
  /// that every approved action is still present and identically bound.
  /// A materially changed plan requires renewed review — approval of the
  /// old set is refused.
  Future<bool> _verifyPlanUnchanged(List<Map<String, dynamic>> approved) async {
    final fresh = await widget.controller
        .planStorageGoal(widget.goalGb, excludeIds: _rejected.toList());
    if (fresh == null) return false;
    final replanned =
        ((fresh['selected'] as List<dynamic>?) ?? []).cast<Map<String, dynamic>>();
    return approvedSetIntact(approved, replanned);
  }

  Future<void> _approve() async {
    final plan = widget.controller.storagePlanResult ?? widget.plan;
    final approvedRaw = _selectedRaw(plan);
    final approved = _selected(plan);
    setState(() => _busy = true);

    if (!await _verifyPlanUnchanged(approvedRaw)) {
      if (!mounted) return;
      setState(() => _busy = false);
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
          content: Text(
              'Plan changed since review — please re-review before approving.')));
      return;
    }

    var totalCandidates = 0;
    var totalBytes = 0;
    for (final r in approved) {
      totalCandidates += r.action?.candidateCount ?? 0;
      totalBytes += r.estimatedLogicalBytes;
    }
    if (requiresBatchConfirm(totalCandidates, totalBytes) && mounted) {
      final ok = await showDialog<bool>(
          context: context,
          builder: (ctx) => AlertDialog(
                title: const Text('Confirm large batch'),
                content: Text('This will move $totalCandidates files '
                    '(~${formatBytes(totalBytes)} estimated logical) to '
                    'quarantine. Items are restorable from History.'),
                actions: [
                  TextButton(
                      onPressed: () => Navigator.pop(ctx, false),
                      child: const Text('Cancel')),
                  FilledButton(
                      onPressed: () => Navigator.pop(ctx, true),
                      child: const Text('Confirm')),
                ],
              ));
      if (ok != true) {
        if (mounted) setState(() => _busy = false);
        return;
      }
    }
    final outcomes = await widget.controller.executePlanActions(approved);
    if (!mounted) return;
    Navigator.pop(context);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        content: Text('Executed ${outcomes.where((o) => o['status'] == 'committed').length}'
            ' of ${approved.length} approved actions — see History to review or restore.')));
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
        listenable: widget.controller,
        builder: (context, _) {
          final plan = widget.controller.storagePlanResult ?? widget.plan;
          final selected = _selected(plan);
          final skipped = (plan['skipped'] as List<dynamic>? ?? []);
    final total = selected.fold<int>(
        0, (s, r) => s + r.estimatedLogicalBytes);
    return AlertDialog(
      title: const Text('Review plan'),
      content: SizedBox(
        width: 560,
        child: ListView(shrinkWrap: true, children: [
          Text('Goal: ${widget.goalGb} GB — estimated logical '
              '${formatBytes(total)} across ${selected.length} approved actions. '
              'Estimates are logical bytes, not guaranteed disk recovery.',
              style: Theme.of(context).textTheme.bodySmall),
          const SizedBox(height: 8),
          for (final r in selected)
            CheckboxListTile(
              dense: true,
              value: true,
              title: Text(r.title, overflow: TextOverflow.ellipsis),
              subtitle: Text(
                  '${r.confidence} confidence · ${r.risk} risk · ${r.reversibility} · '
                  '~${formatBytes(r.estimatedLogicalBytes)} · '
                  '${r.action?.candidateCount ?? 0} candidates'
                  '${_previewLine(r)}'
                  '${r.evidence.isNotEmpty ? '\n${r.evidence.join(' · ')}' : ''}',
                  style: Theme.of(context).textTheme.bodySmall),
              onChanged: _busy
                  ? null
                  : (_) {
                      _rejected.add(r.id);
                      _replan();
                    },
            ),
          Text(
              'Quarantine moves files into an app-owned folder on the same '
              'volume — nothing is permanently deleted. Every item can be '
              'restored from History. The engine re-validates each file at '
              'execution; locked, changed or missing files are skipped.',
              style: Theme.of(context).textTheme.bodySmall),
          if (skipped.isNotEmpty) ...[
            const Divider(),
            Text('Excluded / skipped', style: Theme.of(context).textTheme.labelMedium),
            for (final s in skipped)
              ListTile(
                dense: true,
                title: Text('${s['title']}', overflow: TextOverflow.ellipsis),
                subtitle: Text('${s['reason']}',
                    style: Theme.of(context).textTheme.bodySmall),
              ),
          ],
        ]),
      ),
      actions: [
        TextButton(
            onPressed: _busy ? null : () => Navigator.pop(context),
            child: const Text('Cancel')),
        FilledButton(
            onPressed: _busy || selected.isEmpty ? null : _approve,
            child: Text('Approve & execute ${selected.length}')),
      ],
    );
        });
  }

  /// Live preview summary per action: candidate count + expected skips from
  /// the engine's read-only eligibility probe.
  String _previewLine(StorageRecommendation r) {
    final p = _previews[r.id];
    if (p == null) return '';
    if (p['probeSkipped'] == true) {
      return '\npreview: ${p['candidateCount']} candidates '
          '(per-item probe skipped — large set)';
    }
    final cands = (p['candidates'] as List<dynamic>? ?? []);
    var skip = 0;
    final reasons = <String, int>{};
    for (final c in cands) {
      final e = c['eligibility'] as String? ?? 'eligible';
      if (e != 'eligible') {
        skip++;
        reasons[e] = (reasons[e] ?? 0) + 1;
      }
    }
    if (skip == 0) return '\npreview: ${cands.length} all eligible';
    final detail =
        reasons.entries.map((e) => '${e.value} ${e.key}').join(', ');
    return '\npreview: ${cands.length} candidates — $skip expected skip ($detail)';
  }
}

/// Per-action outcomes from an executed plan — mirrors the journaled,
/// per-item truth; never merges moved bytes with freed space.
class _PlanOutcome extends StatelessWidget {
  final EngineController controller;
  const _PlanOutcome({required this.controller});

  @override
  Widget build(BuildContext context) {
    final outcomes = controller.planExecutionOutcomes!;
    var cleaned = 0, failed = 0, moved = 0;
    final reasonCounts = <String, int>{};
    for (final o in outcomes) {
      for (final i in (o['items'] as List<dynamic>? ?? [])) {
        final st = i['status'] as String? ?? '';
        if (st == 'cleaned') {
          cleaned++;
          moved += i['bytesReclaimed'] as int? ?? 0;
        } else {
          failed++;
          final code = i['errorCode'] as String? ?? st;
          reasonCounts[code] = (reasonCounts[code] ?? 0) + 1;
        }
      }
    }
    final delta = controller.freeSpaceDeltaBytes;
    final deltaText = delta == null
        ? 'Observed free-space change: not measured'
        : 'Observed free-space change on volume: '
            '${delta >= 0 ? '+' : ''}${formatBytes(delta.abs())} — '
            'includes unrelated background system activity; not solely '
            'caused by this cleanup. Same-volume quarantine moves are not '
            'expected to free space.';
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('Plan execution', style: Theme.of(context).textTheme.titleSmall),
          for (final o in outcomes)
            Text('${o['id']}: ${o['status']}'
                '${o['reason'] != null ? ' (${o['reason']})' : ''}'),
          Text('Journaled: $cleaned file(s) quarantined, '
              '${formatBytes(moved)} moved'),
          if (failed > 0)
            Text('Not moved: $failed — '
                '${reasonCounts.entries.map((e) => '${e.value} ${e.key}').join(', ')}'),
          Text(deltaText, style: Theme.of(context).textTheme.bodySmall),
          Text('Review or restore quarantined items in History.',
              style: Theme.of(context).textTheme.bodySmall),
        ]),
      ),
    );
  }
}

/// Post-commit outcome: separates quarantined bytes from skipped/failed items
/// and never conflates "moved to quarantine" with "disk space recovered" —
/// same-volume moves do not free space.
class _StorageOutcome extends StatelessWidget {
  final EngineController controller;
  const _StorageOutcome({required this.controller});

  @override
  Widget build(BuildContext context) {
    final items = controller.lastCommitResults!;
    final cleaned = items.where((i) => i.status == 'cleaned').toList();
    final skipped = items.where((i) => i.status != 'cleaned').toList();
    final moved = cleaned.fold<int>(0, (s, i) => s + i.bytesReclaimed);
    final delta = controller.freeSpaceDeltaBytes;
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('Last operation', style: Theme.of(context).textTheme.titleSmall),
            Text('${cleaned.length} moved to quarantine · '
                '${skipped.length} skipped/failed · '
                '${formatBytes(moved)} now held in quarantine'),
            Text(
              'Quarantined files are moved, not deleted — reclaimable only '
              'after purge. Measured free-space change: '
              '${delta == null ? 'not measurable' : formatBytes(delta)}.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            for (final i in skipped.take(5))
              Text('${i.candidateId}: ${i.errorCode ?? i.status}',
                  style: Theme.of(context).textTheme.bodySmall),
          ]),
        ),
      ),
    );
  }
}

class _Consumers extends StatelessWidget {
  final Map<String, dynamic> inv;
  const _Consumers({required this.inv});

  @override
  Widget build(BuildContext context) {
    final scopes = inv['scopes'] as Map<String, dynamic>? ?? {};
    final all = <Map<String, dynamic>>[
      for (final e in scopes.entries)
        for (final n in e.value as List<dynamic>? ?? [])
          (n as Map<String, dynamic>)..['scope'] = e.key,
    ]..sort((a, b) => (b['logicalBytes'] as int).compareTo(a['logicalBytes'] as int));

    return Card(
      child: ListView(
        padding: const EdgeInsets.all(12),
        children: [
          Text('Largest consumers', style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 8),
          if (all.isEmpty) const Text('No directories measured.'),
          for (final n in all.take(20))
            ListTile(
              dense: true,
              title: Text(n['name'] as String? ?? '', overflow: TextOverflow.ellipsis),
              subtitle: Text('${n['scope']}'
                  '${n['categoryHint'] != null ? ' · ${n['categoryHint']}' : ' · unclassified'}'),
              trailing: Text(formatBytes(n['logicalBytes'] as int? ?? 0)),
            ),
        ],
      ),
    );
  }
}

class _Recommendations extends StatelessWidget {
  final List<StorageRecommendation> recs;
  final EngineController controller;
  const _Recommendations({required this.recs, required this.controller});

  static const _conf = {'high': Colors.green, 'medium': Colors.orange, 'low': Colors.grey};
  static const _risk = {'low': Colors.green, 'medium': Colors.orange, 'high': Colors.deepOrange, 'restricted': Colors.red};

  @override
  Widget build(BuildContext context) {
    if (recs.isEmpty) {
      return const Card(child: Center(child: Text('No recommendations.')));
    }
    return ListView.separated(
      itemCount: recs.length,
      separatorBuilder: (_, _) => const Divider(height: 1),
      itemBuilder: (context, i) {
        final r = recs[i];
        final conf = r.confidence;
        final risk = r.risk;
        return ExpansionTile(
          title: Row(children: [
            Chip(
              visualDensity: VisualDensity.compact,
              backgroundColor: (_conf[conf] ?? Colors.grey).withAlpha(40),
              label: Text(conf, style: TextStyle(color: _conf[conf] ?? Colors.grey)),
            ),
            const SizedBox(width: 6),
            Chip(
              visualDensity: VisualDensity.compact,
              backgroundColor: (_risk[risk] ?? Colors.red).withAlpha(40),
              label: Text(risk, style: TextStyle(color: _risk[risk] ?? Colors.red)),
            ),
            const SizedBox(width: 8),
            Expanded(child: Text(r.title, overflow: TextOverflow.ellipsis)),
          ]),
          subtitle: Text('${r.category} · est. ${formatBytes(r.estimatedLogicalBytes)} · ${r.reversibility}'
              '${r.executable ? ' · executable' : ' · review-only'}'),
          childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
          children: [
            Text(r.explanation),
            const SizedBox(height: 8),
            Text('Evidence', style: Theme.of(context).textTheme.labelMedium),
            for (final e in r.evidence)
              Text('• $e', style: Theme.of(context).textTheme.bodySmall),
            if (r.skipReasons.isNotEmpty) ...[
              const SizedBox(height: 4),
              for (final s in r.skipReasons)
                Text('skipped: $s', style: Theme.of(context).textTheme.bodySmall),
            ],
            if (r.executable)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: FilledButton.tonalIcon(
                  onPressed: () => _reviewAndCommit(context, r),
                  icon: const Icon(Icons.archive_outlined),
                  label: Text('Review & quarantine ${r.action!.candidateCount} '
                      'items (${formatBytes(r.action!.eligibleBytes)})'),
                ),
              )
            else
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text('No automated action — manual review only.',
                    style: TextStyle(
                        color: Theme.of(context).colorScheme.outline,
                        fontStyle: FontStyle.italic)),
              ),
          ],
        );
      },
    );
  }

  /// Existing pipeline: re-check session → list candidates → engine preview
  /// → explicit user approval → journaled quarantine commit. Stale or forged
  /// references surface as "no longer available", never as silent mutation.
  Future<void> _reviewAndCommit(
      BuildContext context, StorageRecommendation rec) async {
    final action = rec.action!;

    final results = await controller.storageActionCandidates(action);
    if (!context.mounted) return;
    if (results == null) {
      _unavailable(context);
      return;
    }

    final ids = results.candidates.map((c) => c.id).toList();
    final approvedBytes =
        await controller.previewStorageAction(action.sessionId, ids);
    if (!context.mounted) return;
    if (approvedBytes == null) {
      _unavailable(context);
      return;
    }

    const cap = 40;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text('Quarantine ${ids.length} items?'),
        content: SizedBox(
          width: 420,
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Flexible(
              child: ListView(
                shrinkWrap: true,
                children: [
                  for (final c in results.candidates.take(cap))
                    ListTile(
                      dense: true,
                      title: Text(c.displayName, overflow: TextOverflow.ellipsis),
                      subtitle: Text(categoryLabel(c.category)),
                      trailing: Text(formatBytes(c.estimatedBytes)),
                    ),
                  if (results.candidates.length > cap)
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text('… and ${results.candidates.length - cap} more'),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 8),
            Text('Approved: ${formatBytes(approvedBytes)}. Files are moved to '
                'ClearForge quarantine, not deleted — you can restore them '
                'from History.'),
          ]),
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
    if (confirmed == true) {
      await controller.commitStorageAction(action.sessionId, ids);
    }
  }

  static void _unavailable(BuildContext context) {
    ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
      content: Text('This action is no longer available — run a storage '
          'scan again to get fresh candidates.'),
    ));
  }
}
