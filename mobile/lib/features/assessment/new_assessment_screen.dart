import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../patients/add_patient_screen.dart';
import '../sync/data/app_database.dart';
import 'assessment_draft.dart';
import 'assessment_view.dart';
import 'capture_screen.dart';

/// Start an assessment: optional patient, clinical questions, then capture.
class NewAssessmentScreen extends StatefulWidget {
  const NewAssessmentScreen({super.key, required this.services, this.patient});

  final AppServices services;

  /// Pre-linked when started from a patient record.
  final PatientLocalData? patient;

  @override
  State<NewAssessmentScreen> createState() => _NewAssessmentScreenState();
}

class _NewAssessmentScreenState extends State<NewAssessmentScreen> {
  late final AssessmentDraft _draft = AssessmentDraft(patient: widget.patient);

  Future<void> _pickPatient() async {
    final picked = await showModalBottomSheet<_PatientChoice>(
      context: context,
      isScrollControlled: true,
      backgroundColor: WoundColors.bg,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(WoundRadii.l))),
      builder: (_) => _PatientPicker(services: widget.services),
    );
    if (picked == null || !mounted) return;
    if (picked.addNew) {
      final created = await Navigator.of(
        context,
      ).push<PatientLocalData>(MaterialPageRoute(builder: (_) => AddPatientScreen(services: widget.services)));
      if (created != null) setState(() => _draft.patient = created);
    } else {
      setState(() => _draft.patient = picked.patient);
    }
  }

  void _capture() => Navigator.of(context).push(
    MaterialPageRoute<void>(
      builder: (_) => CaptureScreen(services: widget.services, draft: _draft),
    ),
  );

  @override
  Widget build(BuildContext context) {
    final p = _draft.patient;
    return Scaffold(
      appBar: AppBar(title: const Text('New Assessment')),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
        children: [
          const FieldLabel('Assessment ID'),
          TextField(
            enabled: false,
            controller: TextEditingController(text: AssessmentView.displayIdOf(_draft.assessmentId)),
            decoration: const InputDecoration(filled: true, fillColor: WoundColors.surfaceAlt),
          ),
          const SizedBox(height: 18),
          const FieldLabel('Patient (optional)'),
          if (p == null)
            OutlinedButton.icon(
              key: const Key('linkPatient'),
              onPressed: _pickPatient,
              icon: const Icon(Icons.link_rounded, size: 18),
              label: const Text('Link patient'),
            )
          else
            WoundCard(
              padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 10),
              child: Row(
                children: [
                  InitialsAvatar(p.displayAlias, size: 34),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          p.displayAlias,
                          key: const Key('linkedPatient'),
                          style: const TextStyle(fontWeight: FontWeight.w700),
                        ),
                        Text(p.patientRef, style: Theme.of(context).textTheme.bodySmall),
                      ],
                    ),
                  ),
                  IconButton(
                    key: const Key('unlinkPatient'),
                    tooltip: 'Unlink',
                    onPressed: () => setState(() => _draft.patient = null),
                    icon: const Icon(Icons.link_off_rounded, size: 18, color: WoundColors.textSecondary),
                  ),
                ],
              ),
            ),
          const SizedBox(height: 22),
          const SectionTitle('Clinical assessment'),
          TriStateQuestion(
            key: const Key('pedalPulses'),
            label: 'Pedal pulses',
            value: _draft.pedalPulses,
            onChanged: (v) => setState(() => _draft.pedalPulses = v),
          ),
          const SizedBox(height: 14),
          TriStateQuestion(
            key: const Key('protectiveSensation'),
            label: 'Protective sensation',
            value: _draft.protectiveSensation,
            onChanged: (v) => setState(() => _draft.protectiveSensation = v),
          ),
          const SizedBox(height: 18),
          const NoticeStrip(
            'Only the information needed for wound assessment is collected. Linking a patient is '
            'optional; patients are shown by the label you choose, never by name.',
          ),
          const SizedBox(height: 22),
          const SectionTitle('Image source'),
          DecoratedBox(
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(WoundRadii.m),
              boxShadow: WoundShadows.accent,
            ),
            child: FilledButton.icon(
              key: const Key('takePhoto'),
              onPressed: _capture,
              icon: const Icon(Icons.photo_camera_outlined, size: 20),
              label: const Text('Take Photo'),
            ),
          ),
          const SizedBox(height: 10),
          OutlinedButton.icon(
            onPressed: _capture,
            icon: const Icon(Icons.image_outlined, size: 20),
            label: const Text('Choose from Device'),
          ),
        ],
      ),
    );
  }
}

/// Present / Absent / Not recorded, as three equal choices (§3: "not recorded" is never collapsed into "no").
class TriStateQuestion extends StatelessWidget {
  const TriStateQuestion({super.key, required this.label, required this.value, required this.onChanged});

  final String label;
  final String value;
  final ValueChanged<String> onChanged;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      FieldLabel(label),
      Row(
        children: [
          for (final (v, text) in const [
            ('present', 'Present'),
            ('absent', 'Absent'),
            ('not_recorded', 'Not recorded'),
          ])
            Expanded(
              child: Padding(
                padding: EdgeInsets.only(right: v == 'not_recorded' ? 0 : 8),
                child: _Choice(text: text, selected: value == v, onTap: () => onChanged(v)),
              ),
            ),
        ],
      ),
    ],
  );
}

class _Choice extends StatelessWidget {
  const _Choice({required this.text, required this.selected, required this.onTap});

  final String text;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Material(
    color: selected ? WoundColors.accentSoft : WoundColors.surface,
    borderRadius: BorderRadius.circular(WoundRadii.field),
    child: InkWell(
      borderRadius: BorderRadius.circular(WoundRadii.field),
      onTap: onTap,
      child: Container(
        height: 44,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(WoundRadii.field),
          border: Border.all(
            color: selected ? WoundColors.accent : WoundColors.borderStrong,
            width: selected ? 1.8 : 1.5,
          ),
        ),
        child: Text(
          text,
          style: TextStyle(
            fontSize: 13,
            fontWeight: FontWeight.w700,
            color: selected ? WoundColors.accentDark : WoundColors.textSecondary,
          ),
        ),
      ),
    ),
  );
}

class _PatientChoice {
  const _PatientChoice.patient(this.patient) : addNew = false;
  const _PatientChoice.addNew() : patient = null, addNew = true;

  final PatientLocalData? patient;
  final bool addNew;
}

class _PatientPicker extends StatefulWidget {
  const _PatientPicker({required this.services});

  final AppServices services;

  @override
  State<_PatientPicker> createState() => _PatientPickerState();
}

class _PatientPickerState extends State<_PatientPicker> {
  String _query = '';

  @override
  Widget build(BuildContext context) => SafeArea(
    child: Padding(
      padding: EdgeInsets.fromLTRB(20, 14, 20, 16 + MediaQuery.viewInsetsOf(context).bottom),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Center(
            child: Container(
              width: 36,
              height: 4,
              decoration: BoxDecoration(color: WoundColors.borderStrong, borderRadius: BorderRadius.circular(2)),
            ),
          ),
          const SizedBox(height: 14),
          Text('Link patient', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 12),
          TextField(
            decoration: const InputDecoration(
              hintText: 'Search label or ID',
              prefixIcon: Icon(Icons.search_rounded, color: WoundColors.textTertiary),
            ),
            onChanged: (v) => setState(() => _query = v.trim().toLowerCase()),
          ),
          const SizedBox(height: 10),
          ConstrainedBox(
            constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * 0.4),
            child: StreamBuilder<List<PatientLocalData>>(
              stream: widget.services.patients.watchAll(),
              builder: (context, snap) {
                final list = (snap.data ?? const <PatientLocalData>[])
                    .where((p) => p.displayAlias.toLowerCase().contains(_query) || p.patientRef.contains(_query))
                    .toList();
                if (list.isEmpty) {
                  return const Padding(
                    padding: EdgeInsets.all(16),
                    child: Text('No patients found. Add one below.', textAlign: TextAlign.center),
                  );
                }
                return ListView.separated(
                  shrinkWrap: true,
                  itemCount: list.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 8),
                  itemBuilder: (context, i) => WoundCard(
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                    onTap: () => Navigator.pop(context, _PatientChoice.patient(list[i])),
                    child: Row(
                      children: [
                        InitialsAvatar(list[i].displayAlias, size: 34),
                        const SizedBox(width: 10),
                        Expanded(
                          child: Text(list[i].displayAlias, style: const TextStyle(fontWeight: FontWeight.w700)),
                        ),
                        Text(list[i].patientRef, style: Theme.of(context).textTheme.bodySmall),
                      ],
                    ),
                  ),
                );
              },
            ),
          ),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            key: const Key('addPatientFromPicker'),
            onPressed: () => Navigator.pop(context, const _PatientChoice.addNew()),
            icon: const Icon(Icons.person_add_alt_rounded, size: 18),
            label: const Text('Add new patient'),
          ),
        ],
      ),
    ),
  );
}
