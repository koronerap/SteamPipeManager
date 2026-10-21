import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

// Sürüm masaüstüyle aynı kaynaktan: src/Product.props. İki yerde tutulsa biri unutulurdu.
val productVersion: String = run {
    val props = rootDir.resolve("../src/Product.props").readText()
    Regex("<Version>([0-9]+\\.[0-9]+\\.[0-9]+)</Version>").find(props)?.groupValues?.get(1)
        ?: error("src/Product.props içinde <Version> bulunamadı")
}

val productVersionCode: Int = productVersion.split(".").map { it.toInt() }.let { (major, minor, patch) ->
    major * 10_000 + minor * 100 + patch
}

// Yayın imzası ortam değişkenlerinden; anahtar deponun dışında. Yoksa yayın derlemesi hata
// ayıklama anahtarıyla imzalanıyor: kurulup denenebiliyor ama gerçek sürümün yerine geçemiyor.
val releaseKeystore: String? = System.getenv("PULSE_KEYSTORE")

android {
    namespace = "io.github.koronerap.pulse"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.koronerap.pulse"
        minSdk = 26
        targetSdk = 35
        versionCode = productVersionCode
        versionName = productVersion
    }

    signingConfigs {
        if (releaseKeystore != null) {
            create("release") {
                storeFile = file(releaseKeystore)
                storePassword = System.getenv("PULSE_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("PULSE_KEY_ALIAS")
                keyPassword = System.getenv("PULSE_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            // Gerçek anahtar yoksa hata ayıklama anahtarıyla: yerelde kurulup denenebilsin.
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.12.01")
    implementation(composeBom)
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")

    implementation("androidx.glance:glance-appwidget:1.1.1")
    implementation("androidx.glance:glance-material3:1.1.1")
    implementation("androidx.work:work-runtime-ktx:2.10.0")

    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")

    // QR tarama: Google Play hizmetleri olmayan telefonlarda da çalışıyor.
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("com.google.zxing:core:3.5.3")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.9.0")
}
