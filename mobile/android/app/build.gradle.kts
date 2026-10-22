plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

android {
    namespace = "com.example.melanin_wound_cdss"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        // TODO: Specify your own unique Application ID (https://developer.android.com/studio/build/application-id.html).
        applicationId = "com.example.melanin_wound_cdss"
        // Adjust these values for your app; see https://flutter.dev/to/review-gradle-config.
        minSdk = maxOf(flutter.minSdkVersion, 23) // flutter_secure_storage needs 23+ (Android Keystore)
        targetSdk = flutter.targetSdkVersion
        // The version code comes from pubspec.yaml. Pass -P force-version-code-ignoring-abi=true to override it.
        versionCode = flutter.versionCode
        versionName = flutter.versionName
        // After sign-in the identity service sends the browser to lk.sliit.cdss:/oauth2redirect, which opens the app.
        manifestPlaceholders["appAuthRedirectScheme"] = "lk.sliit.cdss"
    }

    buildTypes {
        release {
            // TODO: add a real release signing config. Debug keys are used for now so `flutter run --release` works.
            signingConfig = signingConfigs.getByName("debug")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}
