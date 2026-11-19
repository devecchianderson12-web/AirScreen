import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Version is injected by CI from the git tag (vX.Y.Z); local builds use 0.1.0.
val appVersion: String = (project.findProperty("appVersion") as String?) ?: "0.1.0"
val parts = appVersion.split(".").map { it.toIntOrNull() ?: 0 } + listOf(0, 0, 0)
val appVersionCode = parts[0] * 10000 + parts[1] * 100 + parts[2]

val keystoreProps = Properties().apply {
    val f = rootProject.file("keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}

android {
    namespace = "com.dualscreen.app"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.dualscreen.app"
        minSdk = 26
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersion
    }

    signingConfigs {
        create("release") {
            fun env(name: String) = System.getenv(name)?.takeIf { it.isNotEmpty() }
            val path = env("ANDROID_KEYSTORE_PATH") ?: keystoreProps.getProperty("storeFile")
            if (path != null) {
                storeFile = file(path)
                storePassword = env("ANDROID_KEYSTORE_PASSWORD") ?: keystoreProps.getProperty("storePassword")
                // Single-password keystores: alias defaults to "dualscreen", key password to the store password.
                keyAlias = env("ANDROID_KEY_ALIAS") ?: keystoreProps.getProperty("keyAlias") ?: "dualscreen"
                keyPassword = env("ANDROID_KEY_PASSWORD") ?: keystoreProps.getProperty("keyPassword") ?: storePassword
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (signingConfigs.getByName("release").storeFile != null)
                signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    packaging { jniLibs { useLegacyPackaging = false } }
}

dependencies {
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.google.zxing:core:3.5.3")
    implementation("io.github.webrtc-sdk:android:125.6422.07")
}
