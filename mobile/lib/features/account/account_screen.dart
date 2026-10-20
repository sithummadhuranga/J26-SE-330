import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../profile/profile_screen.dart';
import '../sync/api/sync_api.dart';
import 'account_api.dart';

/// Your own account: details, name, password and where you're signed in. It talks to the server directly,
/// so unlike the rest of the app it needs a connection.
class AccountScreen extends StatefulWidget {
  const AccountScreen({super.key, required this.services, required this.onSignedOut});

  final AppServices services;

  /// Called after something here ended this phone's session (a password change or signing out everywhere).
  final VoidCallback onSignedOut;

  @override
  State<AccountScreen> createState() => _AccountScreenState();
}

class _AccountScreenState extends State<AccountScreen> {
  MyProfile? _profile;
  List<MySession>? _sessions;
  String? _thisDevice;
  Object? _error;
  bool _busy = false;

  AccountApi get _api => widget.services.account;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => _error = null);
    try {
      final results = await Future.wait([_api.profile(), _api.sessions(), widget.services.queue.deviceId()]);
      if (!mounted) return;
      setState(() {
        _profile = results[0] as MyProfile;
        _sessions = results[1] as List<MySession>;
        _thisDevice = results[2] as String;
      });
    } catch (e) {
      if (mounted) setState(() => _error = e);
    }
  }

  void _toast(String text) =>
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text), behavior: SnackBarBehavior.floating));

  /// The server has ended this phone's session, so sign out here too and tell the clinician why.
  /// Saved assessments stay in the queue and sync after the next sign-in.
  Future<void> _signedOutBecause(String why) async {
    await widget.services.signOut();
    if (!mounted) return;
    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (dialog) => AlertDialog(
        backgroundColor: WoundColors.surface,
        title: const Text('Signed out'),
        content: Text('$why Assessments saved on this phone are kept and sync once you sign in.'),
        actions: [
          FilledButton(key: const Key('signedOutOk'), onPressed: () => Navigator.pop(dialog), child: const Text('OK')),
        ],
      ),
    );
    if (!mounted) return;
    Navigator.of(context).popUntil((route) => route.isFirst);
    widget.onSignedOut();
  }

  Future<void> _rename() async {
    final saved = await showDialog<MyProfile>(
      context: context,
      builder: (_) => _EditNameDialog(api: _api, current: _profile!.fullName),
    );
    if (saved == null || !mounted) return;
    setState(() => _profile = saved);
    _toast('Name updated');
  }

  Future<void> _changePassword() async {
    final changed = await showDialog<bool>(
      context: context,
      builder: (_) => _ChangePasswordDialog(api: _api),
    );
    if (changed == true) await _signedOutBecause('Your password was changed, so every session ended, this one too.');
  }

  Future<void> _end(MySession session) async {
    final thisPhone = session.deviceId == _thisDevice;
    if (!await _confirm(
      thisPhone ? 'Sign out this phone?' : 'End this session?',
      thisPhone
          ? 'You\'ll need to sign in again to sync.'
          : session.client == 'admin-dashboard'
          ? 'The admin dashboard will have to sign in again.'
          : 'That phone will have to sign in again.',
      'End session',
    )) {
      return;
    }
    await _guarded(() async {
      await _api.endSession(session.sessionId);
      if (thisPhone) {
        await _signedOutBecause('You ended this phone\'s session.');
      } else {
        _toast('Session ended');
        await _load();
      }
    });
  }

  Future<void> _endAll() async {
    if (!await _confirm(
      'Sign out everywhere?',
      'This ends every session, on all phones and the dashboard, including this one.',
      'Sign out everywhere',
    )) {
      return;
    }
    await _guarded(() async {
      await _api.endAllSessions();
      await _signedOutBecause('You signed out everywhere.');
    });
  }

  /// Runs a call that changes something, showing a spinner and turning any error into a message.
  Future<void> _guarded(Future<void> Function() action) async {
    setState(() => _busy = true);
    try {
      await action();
    } catch (e) {
      if (mounted) _toast(accountMessage(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<bool> _confirm(String title, String body, String action) async =>
      await showDialog<bool>(
        context: context,
        builder: (dialog) => AlertDialog(
          backgroundColor: WoundColors.surface,
          title: Text(title),
          content: Text(body),
          actions: [
            TextButton(onPressed: () => Navigator.pop(dialog, false), child: const Text('Cancel')),
            FilledButton(key: const Key('confirm'), onPressed: () => Navigator.pop(dialog, true), child: Text(action)),
          ],
        ),
      ) ??
      false;

  String _sessionName(MySession s) => switch (s) {
    MySession(deviceId: final id?) when id == _thisDevice => 'This phone',
    MySession(client: 'admin-dashboard') => 'Admin dashboard',
    _ => 'Another phone',
  };

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Account')),
    body: switch ((_profile, _error)) {
      (null, null) => const Center(child: CircularProgressIndicator()),
      (null, final error?) => _LoadError(message: accountMessage(error), onRetry: _load),
      (final profile?, _) => RefreshIndicator(
        onRefresh: _load,
        child: AbsorbPointer(
          absorbing: _busy,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 28),
            children: [
              const SectionTitle('Profile'),
              WoundCard(
                child: Column(
                  children: [
                    KeyValueRow('Full name', Text(profile.fullName, key: const Key('fullName'), style: _value)),
                    KeyValueRow('Username', Text(profile.username, style: _value)),
                    KeyValueRow('Role', Text(ProfileScreen.roleLabel(profile.role), style: _value)),
                    KeyValueRow('Facility', Text(profile.facilityId, style: _value)),
                    KeyValueRow(
                      'Two-step sign-in',
                      WoundBadge(
                        profile.mfaEnabled ? 'On' : 'Off',
                        tone: profile.mfaEnabled ? BadgeTone.success : BadgeTone.neutral,
                      ),
                    ),
                    if (profile.lastSignInAt != null)
                      KeyValueRow(
                        'Last sign-in',
                        Text(
                          '${formatDate(profile.lastSignInAt!)}, ${formatTime(profile.lastSignInAt!)}',
                          style: _value,
                        ),
                      ),
                  ],
                ),
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                      key: const Key('editName'),
                      onPressed: _rename,
                      child: const Text('Edit name'),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: OutlinedButton(
                      key: const Key('changePassword'),
                      onPressed: _changePassword,
                      child: const Text('Change password'),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 26),
              SectionTitle('Your sessions', trailing: Text('${_sessions!.length} active', style: _hint)),
              WoundCard(
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
                child: Column(
                  children: [
                    for (final (i, s) in _sessions!.indexed) ...[
                      if (i > 0) const Divider(),
                      _SessionRow(
                        key: Key('session-${s.sessionId}'),
                        name: _sessionName(s),
                        icon: s.client == 'admin-dashboard' ? Icons.computer_rounded : Icons.smartphone_rounded,
                        detail:
                            'Signed in ${formatDate(s.signedInAt)} · last used ${formatDate(s.lastUsedAt)}, '
                            '${formatTime(s.lastUsedAt)}',
                        onEnd: () => _end(s),
                      ),
                    ],
                  ],
                ),
              ),
              const SizedBox(height: 12),
              OutlinedButton.icon(
                key: const Key('signOutEverywhere'),
                onPressed: _endAll,
                icon: const Icon(Icons.logout_rounded, size: 18),
                label: const Text('Sign out everywhere'),
              ),
              const SizedBox(height: 16),
              const NoticeStrip(
                'Changing your password or signing out everywhere ends every session, including this one. '
                'Assessments saved on this phone are kept and sync after you sign in again.',
                icon: Icons.info_outline_rounded,
              ),
            ],
          ),
        ),
      ),
    },
  );

  static const _value = TextStyle(fontSize: 13.5, fontWeight: FontWeight.w600);
  static const _hint = TextStyle(fontSize: 12, color: WoundColors.textTertiary);
}

class _SessionRow extends StatelessWidget {
  const _SessionRow({super.key, required this.name, required this.icon, required this.detail, required this.onEnd});

  final String name;
  final IconData icon;
  final String detail;
  final VoidCallback onEnd;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 10),
    child: Row(
      children: [
        Icon(icon, size: 20, color: WoundColors.textSecondary),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(name, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14)),
              const SizedBox(height: 2),
              Text(detail, style: Theme.of(context).textTheme.bodySmall),
            ],
          ),
        ),
        TextButton(onPressed: onEnd, child: const Text('End')),
      ],
    ),
  );
}

class _LoadError extends StatelessWidget {
  const _LoadError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          EmptyState(icon: Icons.cloud_off_rounded, title: 'Couldn\'t load your account', message: message),
          const SizedBox(height: 12),
          FilledButton(key: const Key('retry'), onPressed: onRetry, child: const Text('Try again')),
        ],
      ),
    ),
  );
}

/// Changes your own full name. Closes with the updated profile.
class _EditNameDialog extends StatefulWidget {
  const _EditNameDialog({required this.api, required this.current});

  final AccountApi api;
  final String current;

  @override
  State<_EditNameDialog> createState() => _EditNameDialogState();
}

class _EditNameDialogState extends State<_EditNameDialog> {
  late final _name = TextEditingController(text: widget.current);
  String? _error;
  bool _busy = false;

  @override
  void dispose() {
    _name.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final name = _name.text.trim();
    final problem = name.isEmpty
        ? 'Enter your full name.'
        : name.length > maxFullNameLength
        ? 'Your full name can be at most $maxFullNameLength characters.'
        : null;
    if (problem != null) {
      setState(() => _error = problem);
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final profile = await widget.api.rename(name);
      if (mounted) Navigator.pop(context, profile);
    } catch (e) {
      if (mounted) setState(() => _error = accountMessage(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    backgroundColor: WoundColors.surface,
    title: const Text('Edit name'),
    content: TextField(
      key: const Key('nameField'),
      controller: _name,
      autofocus: true,
      textCapitalization: TextCapitalization.words,
      decoration: InputDecoration(labelText: 'Full name', errorText: _error),
      onSubmitted: (_) => _save(),
    ),
    actions: [
      TextButton(onPressed: _busy ? null : () => Navigator.pop(context), child: const Text('Cancel')),
      FilledButton(key: const Key('saveName'), onPressed: _busy ? null : _save, child: const Text('Save')),
    ],
  );
}

/// Changes your own password. Closes with true once the server has accepted it.
class _ChangePasswordDialog extends StatefulWidget {
  const _ChangePasswordDialog({required this.api});

  final AccountApi api;

  @override
  State<_ChangePasswordDialog> createState() => _ChangePasswordDialogState();
}

class _ChangePasswordDialogState extends State<_ChangePasswordDialog> {
  final _current = TextEditingController();
  final _next = TextEditingController();
  final _confirm = TextEditingController();
  String? _currentError;
  String? _nextError;
  String? _confirmError;
  bool _busy = false;

  @override
  void dispose() {
    _current.dispose();
    _next.dispose();
    _confirm.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _currentError = _current.text.isEmpty ? 'Enter your current password.' : null;
      _nextError = _next.text.length < minPasswordLength ? 'At least $minPasswordLength characters.' : null;
      _confirmError = _confirm.text != _next.text ? 'The passwords don\'t match.' : null;
    });
    if (_currentError != null || _nextError != null || _confirmError != null) return;
    setState(() => _busy = true);
    try {
      await widget.api.changePassword(_current.text, _next.text);
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      // A wrong current password belongs under that field. Anything else goes under the new one.
      if (mounted) {
        setState(() {
          if (e is ApiException && e.code == 'INVALID_CREDENTIALS') {
            _currentError = accountMessage(e);
          } else {
            _nextError = accountMessage(e);
          }
        });
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Widget _field(String label, TextEditingController c, String? error, Key key) => Padding(
    padding: const EdgeInsets.only(bottom: 10),
    child: TextField(
      key: key,
      controller: c,
      obscureText: true,
      autocorrect: false,
      enableSuggestions: false,
      decoration: InputDecoration(labelText: label, errorText: error, errorMaxLines: 3),
    ),
  );

  @override
  Widget build(BuildContext context) => AlertDialog(
    backgroundColor: WoundColors.surface,
    title: const Text('Change password'),
    content: SingleChildScrollView(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          _field('Current password', _current, _currentError, const Key('currentPassword')),
          _field('New password', _next, _nextError, const Key('newPassword')),
          _field('Confirm new password', _confirm, _confirmError, const Key('confirmPassword')),
          Text(
            'At least $minPasswordLength characters. You\'ll be signed out everywhere and sign in again '
            'with the new password.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ],
      ),
    ),
    actions: [
      TextButton(onPressed: _busy ? null : () => Navigator.pop(context), child: const Text('Cancel')),
      FilledButton(key: const Key('savePassword'), onPressed: _busy ? null : _save, child: const Text('Change')),
    ],
  );
}
