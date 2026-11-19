plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val runNumber = (System.getenv("GITHUB_RUN_NUMBER") ?: "1").toInt()

android {
    namespace = "dev.dictate.grokvoice"
    compileSdk = 35

    defaultConfig {
        applicationId = "dev.dictate.grokvoice"
        minSdk = 31
        targetSdk = 35
        versionCode = runNumber
        versionName = "0.1.$runNumber"
    }

    signingConfigs {
        // Fixed test key so each new build installs over the last one.
        // Test builds only: swap for a key kept in a GitHub secret before anything real.
        create("test") {
            storeFile = rootProject.file("keystore/grokvoice-test.jks")
            storePassword = "grokvoice-test"
            keyAlias = "grokvoice"
            keyPassword = "grokvoice-test"
        }
    }

    buildTypes {
        getByName("release") {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("test")
        }
        getByName("debug") {
            signingConfig = signingConfigs.getByName("test")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}
