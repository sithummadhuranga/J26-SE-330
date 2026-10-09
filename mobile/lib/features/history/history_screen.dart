import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../home/home_screen.dart';
import '../../core/widgets.dart';

/// Every assessment on this phone, newest first, with where each is in the sync.
class HistoryScreen extends StatelessWidget {
  const HistoryScreen({super.key, required this.services});

  final AppServices services;

  @override
  Widget build(BuildContext context) => SafeArea(
    child: Column(
      children: [
        const TabHeader(title: 'Assessment History'),
        Expanded(
          child: RefreshIndicator(
            onRefresh: services.scheduler.syncNow,
            child: RecentAssessments(
              services: services,
              scrollable: true,
              padding: const EdgeInsets.fromLTRB(20, 14, 20, 24),
            ),
          ),
        ),
      ],
    ),
  );
}
