import 'package:flutter/material.dart';

import '../../../app/app_services.dart';
import '../../../core/theme.dart';
import '../api/sync_api.dart';
import '../auth/browser_sign_in.dart';

/// Sign-in screen: opens the identity service's sign-in page (ADR 0006), where the password and, if MFA is on, the
/// authenticator code are entered. The app never sees them.
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

  static const offlineMessage = 'Cannot reach the server. Your saved assessments are safe; sign in when online.';
  static const failedMessage = 'Could not sign in. Please try again.';

  @override
  State<SignInScreen> createState() => _SignInScreenState();
}

class _SignInScreenState extends State<SignInScreen> {
  bool _busy = false;
  String? _error;

  Future<void> _submit() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await widget.services.signIn();
      widget.onSignedIn();
    } on SignInCancelled {
      // They closed the sign-in page. Nothing to report.
    } on TransportException {
      setState(() => _error = SignInScreen.offlineMessage);
    } on ApiException {
      setState(() => _error = SignInScreen.failedMessage);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
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
                  const Text(
                    'You sign in on the hospital sign-in page, which opens in your browser. '
                    'If your account uses an authenticator app, you will be asked for the code there.',
                    key: Key('signInHint'),
                    textAlign: TextAlign.center,
                    style: TextStyle(fontSize: 13, color: WoundColors.textSecondary),
                  ),
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
