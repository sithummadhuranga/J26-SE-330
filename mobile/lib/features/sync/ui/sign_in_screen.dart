import 'package:flutter/material.dart';

import '../../../app/app_services.dart';
import '../../../core/theme.dart';
import '../../../core/widgets.dart';
import '../api/sync_api.dart';

/// Sign-in screen; the MFA code field appears only when the server asks for it.
class SignInScreen extends StatefulWidget {
  const SignInScreen({super.key, required this.services, required this.onSignedIn, this.onCancel});

  final AppServices services;
  final VoidCallback onSignedIn;

  /// Set when signing in again on top of the app (see [again]): shows a close button and says the work is kept.
  final VoidCallback? onCancel;

  /// Re-sign-in shown over the app after a session ends; the queue and screens stay put.
  static Future<void> again(BuildContext context, AppServices services) =>
      Navigator.of(context).push(MaterialPageRoute<void>(
        fullscreenDialog: true,
        builder: (route) => SignInScreen(
          services: services,
          onCancel: () => Navigator.of(route).pop(),
          onSignedIn: () {
            Navigator.of(route).pop();
            services.scheduler.syncNow();
          },
        ),
      ));

  /// The backend's reason codes (contracts/auth.schema.json), in words a clinician can act on.
  static String message(String? code) => switch (code) {
        'INVALID_CREDENTIALS' => 'Username or password is not right.',
        'CREDENTIAL_LOCKED' => 'Too many failed attempts. The account is locked for 15 minutes; an admin can unlock it.',
        'MFA_REQUIRED' => 'Enter the 6-digit code from your authenticator app.',
        'INVALID_TOTP' => 'That code is not right or has expired. Try the current one.',
        'DEVICE_NOT_ALLOWED' => 'This phone is not allowed for your facility. Ask an admin.',
        _ => 'Could not sign in. Please try again.',
      };

  @override
  State<SignInScreen> createState() => _SignInScreenState();
}

class _SignInScreenState extends State<SignInScreen> {
  final _username = TextEditingController();
  final _password = TextEditingController();
  final _code = TextEditingController();
  bool _needsCode = false;
  bool _busy = false;
  String? _error;

  Future<void> _submit() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await widget.services.signIn(
        username: _username.text.trim(),
        password: _password.text,
        totp: _needsCode ? _code.text.trim() : null,
      );
      widget.onSignedIn();
    } on ApiException catch (e) {
      setState(() {
        _needsCode = _needsCode || e.code == 'MFA_REQUIRED' || e.code == 'INVALID_TOTP';
        _error = SignInScreen.message(e.code);
      });
    } on TransportException {
      setState(() => _error = 'Cannot reach the server. Your saved assessments are safe; sign in when online.');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    _code.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: widget.onCancel == null
            ? null
            : AppBar(
                leading: IconButton(
                    key: const Key('signInCancel'), icon: const Icon(Icons.close_rounded), onPressed: widget.onCancel),
              ),
        body: SafeArea(
          child: Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(24, 32, 24, 24),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 420),
                child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                  const Center(child: BrandMark()),
                  const SizedBox(height: 22),
                  Text('WoundAI', textAlign: TextAlign.center, style: Theme.of(context).textTheme.headlineSmall),
                  const SizedBox(height: 4),
                  const Text('Clinical wound assessment support',
                      textAlign: TextAlign.center, style: TextStyle(color: WoundColors.textSecondary, fontSize: 14)),
                  if (widget.onCancel != null) ...[
                    const SizedBox(height: 18),
                    const Text(
                      'Your session ended. Assessments saved on this phone are kept and sync once you sign in.',
                      key: Key('signInAgainNote'),
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600),
                    ),
                  ],
                  const SizedBox(height: 28),
                  const FieldLabel('Username'),
                  TextField(
                    key: const Key('username'),
                    controller: _username,
                    decoration: const InputDecoration(hintText: 'e.g. n.silva'),
                    autocorrect: false,
                    textInputAction: TextInputAction.next,
                  ),
                  const SizedBox(height: 16),
                  const FieldLabel('Password'),
                  TextField(
                    key: const Key('password'),
                    controller: _password,
                    decoration: const InputDecoration(hintText: '••••••••'),
                    obscureText: true,
                    onSubmitted: (_) => _busy ? null : _submit(),
                  ),
                  if (_needsCode) ...[
                    const SizedBox(height: 16),
                    const FieldLabel('Authenticator code'),
                    TextField(
                      key: const Key('totp'),
                      controller: _code,
                      decoration: const InputDecoration(hintText: '6-digit code', counterText: ''),
                      keyboardType: TextInputType.number,
                      maxLength: 6,
                    ),
                  ],
                  if (_error != null) ...[
                    const SizedBox(height: 14),
                    Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      const Icon(Icons.error_outline_rounded, size: 16, color: WoundColors.error),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(_error!,
                            key: const Key('signInError'),
                            style: const TextStyle(color: WoundColors.error, fontSize: 13, fontWeight: FontWeight.w600)),
                      ),
                    ]),
                  ],
                  const SizedBox(height: 22),
                  DecoratedBox(
                    decoration: BoxDecoration(
                        borderRadius: BorderRadius.circular(WoundRadii.m), boxShadow: _busy ? null : WoundShadows.accent),
                    child: FilledButton(
                      key: const Key('signInButton'),
                      onPressed: _busy ? null : _submit,
                      child: _busy
                          ? const SizedBox(
                              height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                          : const Text('Sign in'),
                    ),
                  ),
                  const SizedBox(height: 26),
                  const Text('Demo environment — prototype for research evaluation only.',
                      textAlign: TextAlign.center, style: TextStyle(fontSize: 11.5, color: WoundColors.textTertiary)),
                ]),
              ),
            ),
          ),
        ),
      );
}

/// The rounded teal app mark (the prototype's scan icon).
class BrandMark extends StatelessWidget {
  const BrandMark({super.key, this.size = 56});

  final double size;

  @override
  Widget build(BuildContext context) => Container(
        width: size,
        height: size,
        decoration: BoxDecoration(
          gradient: const LinearGradient(
              begin: Alignment.topLeft, end: Alignment.bottomRight, colors: [WoundColors.accent, WoundColors.accentDark]),
          borderRadius: BorderRadius.circular(size * 0.29),
          boxShadow: WoundShadows.accent,
        ),
        child: Icon(Icons.center_focus_strong_outlined, color: Colors.white, size: size * 0.48),
      );
}
