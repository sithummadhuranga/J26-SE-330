import 'package:flutter/material.dart';

/// The WoundAI look: warm paper background, teal actions, slate-blue accent, white cards, Inter font.
abstract final class WoundColors {
  static const accent = Color(0xFF0C6D5E);
  static const accentDark = Color(0xFF084E44);
  static const accentSoft = Color(0xFFDCEEEA);
  static const accent2 = Color(0xFF20465F);
  static const bg = Color(0xFFF2F0E9);
  static const surface = Color(0xFFFFFFFF);
  static const surfaceAlt = Color(0xFFEAE6D9);
  static const surfaceSunken = Color(0xFFE7E2D3);
  static const border = Color(0xFFDEDACB);
  static const borderStrong = Color(0xFFC7C0AA);
  static const text = Color(0xFF1D1C18);
  static const textSecondary = Color(0xFF68655A);
  static const textTertiary = Color(0xFF9B9585);
  static const success = Color(0xFF3D7A52);
  static const successSoft = Color(0xFFE1F0E5);
  static const warning = Color(0xFF946318);
  static const warningSoft = Color(0xFFF6E9D2);
  static const error = Color(0xFFAD3A33);
  static const errorSoft = Color(0xFFF5E0DD);
  static const amber = Color(0xFFC79A4B); // second color region in the distribution bar
}

abstract final class WoundRadii {
  static const s = 8.0;
  static const m = 14.0;
  static const l = 22.0;
  static const field = 11.0;
}

abstract final class WoundShadows {
  static const card = [
    BoxShadow(color: Color(0x0F14120C), blurRadius: 2, offset: Offset(0, 1)),
    BoxShadow(color: Color(0x0A14120C), blurRadius: 1, offset: Offset(0, 1)),
  ];
  static const raised = [BoxShadow(color: Color(0x2414120C), blurRadius: 24, offset: Offset(0, 8))];
  static const accent = [BoxShadow(color: Color(0x470C6D5E), blurRadius: 22, offset: Offset(0, 10))];
}

ThemeData woundTheme() {
  const colors = ColorScheme(
    brightness: Brightness.light,
    primary: WoundColors.accent,
    onPrimary: Colors.white,
    primaryContainer: WoundColors.accentSoft,
    onPrimaryContainer: WoundColors.accentDark,
    secondary: WoundColors.accent2,
    onSecondary: Colors.white,
    surface: WoundColors.surface,
    onSurface: WoundColors.text,
    onSurfaceVariant: WoundColors.textSecondary,
    surfaceContainerLowest: WoundColors.bg,
    surfaceContainerHighest: WoundColors.surfaceAlt,
    outline: WoundColors.borderStrong,
    outlineVariant: WoundColors.border,
    error: WoundColors.error,
    onError: Colors.white,
  );
  final field = OutlineInputBorder(
    borderRadius: BorderRadius.circular(WoundRadii.field),
    borderSide: const BorderSide(color: WoundColors.borderStrong, width: 1.5),
  );
  return ThemeData(
    useMaterial3: true,
    colorScheme: colors,
    fontFamily: 'Inter',
    scaffoldBackgroundColor: WoundColors.bg,
    textTheme: const TextTheme(
      headlineSmall: TextStyle(fontSize: 23, fontWeight: FontWeight.w800, letterSpacing: -0.4, color: WoundColors.text),
      titleLarge: TextStyle(fontSize: 18, fontWeight: FontWeight.w800, letterSpacing: -0.3, color: WoundColors.text),
      titleMedium: TextStyle(fontSize: 15, fontWeight: FontWeight.w700, color: WoundColors.text),
      bodyLarge: TextStyle(fontSize: 14.5, color: WoundColors.text),
      bodyMedium: TextStyle(fontSize: 13.5, color: WoundColors.text, height: 1.45),
      bodySmall: TextStyle(fontSize: 12, color: WoundColors.textSecondary),
      labelLarge: TextStyle(fontSize: 15, fontWeight: FontWeight.w700),
    ),
    appBarTheme: const AppBarTheme(
      backgroundColor: WoundColors.bg,
      foregroundColor: WoundColors.text,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      scrolledUnderElevation: 0,
      centerTitle: false,
      titleTextStyle: TextStyle(
        fontFamily: 'Inter',
        fontSize: 18,
        fontWeight: FontWeight.w800,
        letterSpacing: -0.3,
        color: WoundColors.text,
      ),
      shape: Border(bottom: BorderSide(color: WoundColors.border)),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: WoundColors.surface,
      contentPadding: const EdgeInsets.symmetric(horizontal: 13, vertical: 13),
      hintStyle: const TextStyle(color: WoundColors.textTertiary),
      border: field,
      enabledBorder: field,
      focusedBorder: field.copyWith(borderSide: const BorderSide(color: WoundColors.accent, width: 1.8)),
      errorBorder: field.copyWith(borderSide: const BorderSide(color: WoundColors.error, width: 1.5)),
      focusedErrorBorder: field.copyWith(borderSide: const BorderSide(color: WoundColors.error, width: 1.8)),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        backgroundColor: WoundColors.accent,
        foregroundColor: Colors.white,
        disabledBackgroundColor: WoundColors.surfaceSunken,
        disabledForegroundColor: WoundColors.textTertiary,
        minimumSize: const Size.fromHeight(50),
        textStyle: const TextStyle(fontFamily: 'Inter', fontSize: 15, fontWeight: FontWeight.w700),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(WoundRadii.m)),
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        foregroundColor: WoundColors.accentDark,
        backgroundColor: WoundColors.surface,
        minimumSize: const Size.fromHeight(50),
        side: const BorderSide(color: WoundColors.borderStrong, width: 1.5),
        textStyle: const TextStyle(fontFamily: 'Inter', fontSize: 15, fontWeight: FontWeight.w700),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(WoundRadii.m)),
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(
        foregroundColor: WoundColors.accentDark,
        textStyle: const TextStyle(fontFamily: 'Inter', fontSize: 14, fontWeight: FontWeight.w700),
      ),
    ),
    navigationBarTheme: NavigationBarThemeData(
      backgroundColor: WoundColors.surface,
      surfaceTintColor: Colors.transparent,
      indicatorColor: WoundColors.accentSoft,
      height: 68,
      labelTextStyle: WidgetStateProperty.resolveWith(
        (s) => TextStyle(
          fontFamily: 'Inter',
          fontSize: 11.5,
          fontWeight: FontWeight.w700,
          color: s.contains(WidgetState.selected) ? WoundColors.accentDark : WoundColors.textTertiary,
        ),
      ),
      iconTheme: WidgetStateProperty.resolveWith(
        (s) =>
            IconThemeData(color: s.contains(WidgetState.selected) ? WoundColors.accentDark : WoundColors.textTertiary),
      ),
    ),
    snackBarTheme: SnackBarThemeData(
      behavior: SnackBarBehavior.floating,
      backgroundColor: WoundColors.text,
      contentTextStyle: const TextStyle(fontFamily: 'Inter', color: Colors.white, fontSize: 13.5),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(WoundRadii.m)),
    ),
    dividerTheme: const DividerThemeData(color: WoundColors.border, space: 1, thickness: 1),
    progressIndicatorTheme: const ProgressIndicatorThemeData(color: WoundColors.accent),
  );
}
