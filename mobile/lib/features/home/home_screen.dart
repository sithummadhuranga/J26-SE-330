import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../app/connection.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../assessment/assessment_detail_screen.dart';
import '../assessment/assessment_view.dart';
import '../assessment/new_assessment_screen.dart';
import '../sync/data/app_database.dart';
import '../sync/engine/sync_scheduler.dart';
import '../sync/ui/sign_in_screen.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key, required this.services, required this.onViewAll});

  final AppServices services;
  final VoidCallback onViewAll;

  static String greeting(DateTime now) => now.hour < 12
      ? 'Good morning'
      : now.hour < 17
      ? 'Good afternoon'
      : 'Good evening';

  @override
  Widget build(BuildContext context) => SafeArea(
    child: RefreshIndicator(
      onRefresh: services.scheduler.syncNow,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(20, 18, 20, 24),
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: FutureBuilder<AuthLocalData?>(
                  future: services.cachedClinician(),
                  builder: (context, snap) => Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${greeting(DateTime.now())}${snap.data == null ? '' : ', ${snap.data!.username}'}',
                        key: const Key('greeting'),
                        style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800, letterSpacing: -0.4),
                      ),
                      const SizedBox(height: 2),
                      const Text(
                        'Clinical assessment support',
                        style: TextStyle(fontSize: 13, color: WoundColors.textSecondary),
                      ),
                    ],
                  ),
                ),
              ),
              ValueListenableBuilder<SyncStatus>(
                valueListenable: services.scheduler.status,
                builder: (context, status, _) => ConnectionPill(
                  connection: connectionOf(status),
                  onTap: () => connectionOf(status) == Connection.signInNeeded
                      ? SignInScreen.again(context, services)
                      : services.scheduler.syncNow(),
                ),
              ),
            ],
          ),
          const SizedBox(height: 20),
          _NewAssessmentCard(
            onTap: () => Navigator.of(
              context,
            ).push(MaterialPageRoute<void>(builder: (_) => NewAssessmentScreen(services: services))),
          ),
          const SizedBox(height: 26),
          SectionTitle(
            'Recent assessments',
            trailing: TextButton(key: const Key('viewAll'), onPressed: onViewAll, child: const Text('View all')),
          ),
          RecentAssessments(services: services, limit: 3),
        ],
      ),
    ),
  );
}

class _NewAssessmentCard extends StatelessWidget {
  const _NewAssessmentCard({required this.onTap});

  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(borderRadius: BorderRadius.circular(WoundRadii.l), boxShadow: WoundShadows.accent),
    child: Material(
      borderRadius: BorderRadius.circular(WoundRadii.l),
      clipBehavior: Clip.antiAlias,
      child: Ink(
        decoration: const BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topLeft,
            end: Alignment.bottomRight,
            colors: [WoundColors.accent, WoundColors.accentDark],
          ),
        ),
        child: InkWell(
          key: const Key('newAssessment'),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.all(18),
            child: Row(
              children: [
                Container(
                  width: 42,
                  height: 42,
                  decoration: BoxDecoration(
                    color: Colors.white.withValues(alpha: 0.18),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: const Icon(Icons.add_rounded, color: Colors.white),
                ),
                const SizedBox(width: 14),
                const Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'New Assessment',
                        style: TextStyle(color: Colors.white, fontSize: 16.5, fontWeight: FontWeight.w800),
                      ),
                      SizedBox(height: 2),
                      Text(
                        'Capture and analyze a wound image',
                        style: TextStyle(color: Color(0xD9FFFFFF), fontSize: 13),
                      ),
                    ],
                  ),
                ),
                const Icon(Icons.chevron_right_rounded, color: Colors.white),
              ],
            ),
          ),
        ),
      ),
    ),
  );
}

/// Latest assessments, newest first; [limit] null shows all, [scrollable] builds a lazy list.
class RecentAssessments extends StatelessWidget {
  const RecentAssessments({
    super.key,
    required this.services,
    this.limit,
    this.patientRef,
    this.scrollable = false,
    this.padding = EdgeInsets.zero,
  });

  final bool scrollable;
  final EdgeInsetsGeometry padding;

  final AppServices services;
  final int? limit;

  /// Only this patient's assessments.
  final String? patientRef;

  @override
  Widget build(BuildContext context) => StreamBuilder<List<PatientLocalData>>(
    stream: services.patients.watchAll(),
    builder: (context, patientsSnap) {
      final labels = {for (final p in patientsSnap.data ?? const <PatientLocalData>[]) p.patientRef: p.displayAlias};
      return StreamBuilder<List<QueuedEvent>>(
        stream: services.queue.watchRecent(limit: 500),
        builder: (context, snap) {
          // Scrollable: the loading and empty states scroll too, so pull-to-refresh works on them.
          Widget fixed(Widget child) => scrollable
              ? ListView(physics: const AlwaysScrollableScrollPhysics(), padding: padding, children: [child])
              : child;
          if (!snap.hasData) {
            return fixed(
              const Padding(
                padding: EdgeInsets.all(24),
                child: Center(child: CircularProgressIndicator()),
              ),
            );
          }
          var views = AssessmentView.latestPerAssessment(snap.data!);
          if (patientRef != null) views = views.where((v) => v.patientRef == patientRef).toList();
          if (limit != null) views = views.take(limit!).toList();
          if (views.isEmpty) {
            return fixed(
              EmptyState(
                icon: Icons.photo_camera_outlined,
                title: patientRef == null ? 'No assessments yet' : 'No assessments linked yet',
                message: patientRef == null
                    ? 'Start a new assessment to capture and analyze a wound.'
                    : 'Assessments you link to this patient appear here.',
              ),
            );
          }
          Widget card(AssessmentView v) => Padding(
            padding: const EdgeInsets.only(bottom: 10),
            child: AssessmentCard(
              view: v,
              patientLabel: labels[v.patientRef] ?? 'Unlinked case',
              onTap: () => Navigator.of(context).push(
                MaterialPageRoute<void>(
                  builder: (_) => AssessmentDetailScreen(services: services, assessmentId: v.assessmentId),
                ),
              ),
            ),
          );
          if (scrollable) {
            return ListView.builder(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: padding,
              itemCount: views.length,
              itemBuilder: (_, i) => card(views[i]),
            );
          }
          return Column(children: [for (final v in views) card(v)]);
        },
      );
    },
  );
}
