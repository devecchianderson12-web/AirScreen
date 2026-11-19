pluginManagement {
    repositories {
        google()
        mavenCentral()
        // Google-hosted mirror of Maven Central: used when repo.maven.apache.org throttles CI runners.
        maven("https://maven-central.storage-download.googleapis.com/maven2/")
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        // Google-hosted mirror of Maven Central: used when repo.maven.apache.org throttles CI runners.
        maven("https://maven-central.storage-download.googleapis.com/maven2/")
    }
}
rootProject.name = "DualScreen"
include(":app")
