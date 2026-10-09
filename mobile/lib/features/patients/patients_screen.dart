import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../sync/data/app_database.dart';
import 'add_patient_screen.dart';
import 'patient_detail_screen.dart';

/// Patients on this phone, shown by the clinician's label (no personal details).
class PatientsScreen extends StatefulWidget {
  const PatientsScreen({super.key, required this.services});

  final AppServices services;

  @override
  State<PatientsScreen> createState() => _PatientsScreenState();
}

class _PatientsScreenState extends State<PatientsScreen> {
  String _query = '';

  @override
  Widget build(BuildContext context) => SafeArea(
    child: Column(
      children: [
        TabHeader(
          title: 'Patients',
          action: IconButton.filledTonal(
            key: const Key('addPatient'),
            tooltip: 'Add patient',
            style: IconButton.styleFrom(
              backgroundColor: WoundColors.accentSoft,
              foregroundColor: WoundColors.accentDark,
            ),
            onPressed: () => Navigator.of(
              context,
            ).push(MaterialPageRoute<PatientLocalData>(builder: (_) => AddPatientScreen(services: widget.services))),
            icon: const Icon(Icons.add_rounded),
          ),
        ),
        Expanded(
          child: ListView(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 24),
            children: [
              const NoticeStrip(
                'Patients are shown by a label you choose, such as initials or a bed number. '
                'No names or contact details are stored.',
                icon: Icons.lock_outline_rounded,
              ),
              const SizedBox(height: 14),
              TextField(
                key: const Key('patientSearch'),
                decoration: const InputDecoration(
                  hintText: 'Search label or ID',
                  prefixIcon: Icon(Icons.search_rounded, color: WoundColors.textTertiary),
                ),
                onChanged: (v) => setState(() => _query = v.trim().toLowerCase()),
              ),
              const SizedBox(height: 14),
              StreamBuilder<List<PatientLocalData>>(
                stream: widget.services.patients.watchAll(),
                builder: (context, snap) {
                  final all = snap.data ?? const <PatientLocalData>[];
                  final list = all
                      .where((p) => p.displayAlias.toLowerCase().contains(_query) || p.patientRef.contains(_query))
                      .toList();
                  if (list.isEmpty) {
                    return EmptyState(
                      icon: Icons.people_outline_rounded,
                      title: all.isEmpty ? 'No patients yet' : 'No patients found',
                      message: all.isEmpty
                          ? 'Add a patient to link their assessments together.'
                          : 'Try a different search, or add a new patient.',
                    );
                  }
                  return Column(
                    children: [
                      for (final p in list)
                        Padding(
                          padding: const EdgeInsets.only(bottom: 10),
                          child: WoundCard(
                            key: Key('patient-${p.patientRef}'),
                            padding: const EdgeInsets.all(12),
                            onTap: () => Navigator.of(context).push(
                              MaterialPageRoute<void>(
                                builder: (_) => PatientDetailScreen(services: widget.services, patient: p),
                              ),
                            ),
                            child: Row(
                              children: [
                                InitialsAvatar(p.displayAlias),
                                const SizedBox(width: 12),
                                Expanded(
                                  child: Column(
                                    crossAxisAlignment: CrossAxisAlignment.start,
                                    children: [
                                      Text(
                                        p.displayAlias,
                                        style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14.5),
                                      ),
                                      const SizedBox(height: 2),
                                      Text(
                                        '${p.patientRef} · added ${formatDate(p.createdAt)}',
                                        style: Theme.of(context).textTheme.bodySmall,
                                      ),
                                    ],
                                  ),
                                ),
                                const Icon(Icons.chevron_right_rounded, color: WoundColors.textTertiary),
                              ],
                            ),
                          ),
                        ),
                    ],
                  );
                },
              ),
            ],
          ),
        ),
      ],
    ),
  );
}
