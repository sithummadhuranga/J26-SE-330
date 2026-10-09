import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../assessment/new_assessment_screen.dart';
import '../home/home_screen.dart';
import '../sync/data/app_database.dart';

class PatientDetailScreen extends StatelessWidget {
  const PatientDetailScreen({super.key, required this.services, required this.patient});

  final AppServices services;
  final PatientLocalData patient;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Patient Record')),
    body: ListView(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
      children: [
        Row(
          children: [
            InitialsAvatar(patient.displayAlias, size: 52),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(patient.displayAlias, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
                  const SizedBox(height: 2),
                  Text(patient.patientRef, style: Theme.of(context).textTheme.bodySmall),
                ],
              ),
            ),
          ],
        ),
        const SizedBox(height: 16),
        StreamBuilder<PatientLocalData?>(
          stream: (services.db.select(
            services.db.patientLocal,
          )..where((p) => p.patientRef.equals(patient.patientRef))).watchSingleOrNull(),
          builder: (context, snap) {
            final p = snap.data ?? patient;
            return WoundCard(
              child: Column(
                children: [
                  KeyValueRow('Patient ID', Text(p.patientRef, style: _value)),
                  KeyValueRow('Added', Text(formatDate(p.createdAt), style: _value)),
                  KeyValueRow(
                    'Facility record',
                    p.aliasSyncedAt == null
                        ? const WoundBadge('Waiting to sync', tone: BadgeTone.warning)
                        : const WoundBadge('Shared', tone: BadgeTone.success, icon: Icons.check_rounded),
                  ),
                ],
              ),
            );
          },
        ),
        const SizedBox(height: 22),
        const SectionTitle('Linked assessments'),
        RecentAssessments(services: services, patientRef: patient.patientRef),
        const SizedBox(height: 8),
        DecoratedBox(
          decoration: BoxDecoration(borderRadius: BorderRadius.circular(WoundRadii.m), boxShadow: WoundShadows.accent),
          child: FilledButton.icon(
            key: const Key('newAssessmentForPatient'),
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => NewAssessmentScreen(services: services, patient: patient),
              ),
            ),
            icon: const Icon(Icons.add_rounded),
            label: const Text('New assessment for this patient'),
          ),
        ),
      ],
    ),
  );

  static const _value = TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5);
}
