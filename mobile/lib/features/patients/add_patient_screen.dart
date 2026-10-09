import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../sync/data/patient_repository.dart';

/// Adds a patient by label only; the phone generates a pseudonymous id. Pops with the new patient.
class AddPatientScreen extends StatefulWidget {
  const AddPatientScreen({super.key, required this.services});

  final AppServices services;

  @override
  State<AddPatientScreen> createState() => _AddPatientScreenState();
}

class _AddPatientScreenState extends State<AddPatientScreen> {
  final _label = TextEditingController();
  String? _error;
  bool _busy = false;

  @override
  void dispose() {
    _label.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final created = await widget.services.patients.create(_label.text);
      widget.services.scheduler.onSaved(); // the label reaches the server with the next sync
      if (mounted) Navigator.pop(context, created);
    } on ArgumentError catch (e) {
      setState(() => _error = e.message as String);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Add Patient')),
    body: ListView(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
      children: [
        const FieldLabel('Patient label'),
        TextField(
          key: const Key('patientLabel'),
          controller: _label,
          autofocus: true,
          maxLength: PatientRepository.maxAliasLength,
          textCapitalization: TextCapitalization.characters,
          decoration: InputDecoration(hintText: 'e.g. A.F. — bed 12', errorText: _error, counterText: ''),
          onSubmitted: (_) => _busy ? null : _save(),
        ),
        const SizedBox(height: 6),
        const Text(
          'Use initials or a bed number, not the full name.',
          style: TextStyle(fontSize: 12, color: WoundColors.textSecondary),
        ),
        const SizedBox(height: 18),
        const NoticeStrip(
          'The phone creates a pseudonymous patient ID. Only this label is shared with clinicians at your facility; '
          'it never leaves with the assessment data or reaches the guidance service.',
          icon: Icons.lock_outline_rounded,
        ),
        const SizedBox(height: 22),
        DecoratedBox(
          decoration: BoxDecoration(borderRadius: BorderRadius.circular(WoundRadii.m), boxShadow: WoundShadows.accent),
          child: FilledButton(
            key: const Key('savePatient'),
            onPressed: _busy ? null : _save,
            child: const Text('Save patient'),
          ),
        ),
      ],
    ),
  );
}
