// Pipe Manager Pulse (Android): Steam analitiğini takip eden uygulama ve ana ekran widget'ı.
// Masaüstü uygulamalarından bağımsız derleniyor; sürüm src/Product.props'tan okunuyor.
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "PipeManagerPulse"
include(":app")
