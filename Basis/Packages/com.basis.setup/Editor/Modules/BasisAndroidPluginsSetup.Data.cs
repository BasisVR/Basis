namespace Basis.Setup.Modules
{
    public sealed partial class BasisAndroidPluginsSetup
    {
        private const string ActiveManifest = @"<?xml version=""1.0"" encoding=""utf-8"" standalone=""no""?>
<manifest xmlns:android=""http://schemas.android.com/apk/res/android"" xmlns:tools=""http://schemas.android.com/tools"" android:installLocation=""auto"">
  <application android:label=""@string/app_name"" android:icon=""@mipmap/app_icon"" android:allowBackup=""false"" android:memtagMode=""off"" tools:targetApi=""34"">
    <activity android:theme=""@style/Theme.AppCompat.DayNight.NoActionBar"" android:configChanges=""locale|fontScale|keyboard|keyboardHidden|mcc|mnc|navigation|orientation|screenLayout|screenSize|smallestScreenSize|touchscreen|uiMode"" android:launchMode=""singleTask"" android:name=""com.unity3d.player.UnityPlayerGameActivity"" android:excludeFromRecents=""true"" android:exported=""true"">
      <intent-filter>
        <action android:name=""android.intent.action.MAIN"" />
        <category android:name=""android.intent.category.LAUNCHER"" />
        <category android:name=""com.oculus.intent.category.VR"" />
      </intent-filter>
      <intent-filter>
        <action android:name=""android.intent.action.VIEW"" />
        <category android:name=""android.intent.category.DEFAULT"" />
        <category android:name=""android.intent.category.BROWSABLE"" />
        <data android:scheme=""basisdemo"" />
      </intent-filter>
      <meta-data android:name=""com.oculus.vr.focusaware"" android:value=""true"" />
    </activity>
    <meta-data android:name=""unityplayer.SkipPermissionsDialog"" android:value=""false"" />
    <meta-data android:name=""com.oculus.handtracking.frequency"" android:value=""HIGH"" />
    <meta-data android:name=""com.oculus.handtracking.version"" android:value=""V2.0"" />
    <meta-data android:name=""com.oculus.ossplash.background"" android:value=""black"" />
    <meta-data android:name=""com.oculus.telemetry.project_guid"" android:value=""cb9358b9-49a4-46cc-89a1-1f9eeab060e3"" />
    <meta-data android:name=""com.oculus.supportedDevices"" android:value=""quest2|questpro|quest3|quest3s"" tools:replace=""android:value"" />
  </application>
  <uses-feature android:name=""android.hardware.vr.headtracking"" android:version=""1"" android:required=""false"" />
  <uses-feature android:name=""oculus.software.handtracking"" android:required=""false"" />
  <uses-feature android:name=""com.oculus.feature.PASSTHROUGH"" android:required=""false"" />
  <uses-permission android:name=""com.oculus.permission.HAND_TRACKING"" />
  <uses-permission android:name=""android.permission.INTERNET"" />
</manifest>
";

        private const string TemplateManifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<manifest
    xmlns:android=""http://schemas.android.com/apk/res/android""
    xmlns:tools=""http://schemas.android.com/tools"">
    <application>
        <!--Used when Application Entry is set to Activity, otherwise remove this activity block-->
        <activity android:name=""com.unity3d.player.UnityPlayerActivity""
                  android:theme=""@style/UnityThemeSelector"">
            <intent-filter>
                <action android:name=""android.intent.action.MAIN"" />
                <category android:name=""android.intent.category.LAUNCHER"" />
            </intent-filter>
            <meta-data android:name=""unityplayer.UnityActivity"" android:value=""true"" />
        </activity>
        <!--Used when Application Entry is set to GameActivity, otherwise remove this activity block-->
        <activity android:name=""com.unity3d.player.UnityPlayerGameActivity""
                  android:theme=""@style/BaseUnityGameActivityTheme"">
            <intent-filter>
                <action android:name=""android.intent.action.MAIN"" />
                <category android:name=""android.intent.category.LAUNCHER"" />
            </intent-filter>
            <meta-data android:name=""unityplayer.UnityActivity"" android:value=""true"" />
            <meta-data android:name=""android.app.lib_name"" android:value=""game"" />
        </activity>
    </application>
</manifest>
";

        private const string LauncherManifest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<manifest
    xmlns:android=""http://schemas.android.com/apk/res/android""
    xmlns:tools=""http://schemas.android.com/tools""
    android:installLocation=""preferExternal"">
    <supports-screens
        android:smallScreens=""true""
        android:normalScreens=""true""
        android:largeScreens=""true""
        android:xlargeScreens=""true""
        android:anyDensity=""true""/>

    <application android:label=""@string/app_name""
                 android:icon=""@mipmap/app_icon""/>
</manifest>
";

        private const string BaseProjectTemplate = @"plugins {
    // If you are changing the Android Gradle Plugin version, make sure it is compatible with the Gradle version preinstalled with Unity
    // See which Gradle version is preinstalled with Unity here https://docs.unity3d.com/Manual/android-gradle-overview.html
    // See official Gradle and Android Gradle Plugin compatibility table here https://developer.android.com/studio/releases/gradle-plugin#updating-gradle
    // To specify a custom Gradle version in Unity, go do ""Preferences > External Tools"", uncheck ""Gradle Installed with Unity (recommended)"" and specify a path to a custom Gradle version
    id 'com.android.application' version '8.3.0' apply false
    id 'com.android.library' version '8.3.0' apply false
    **BUILD_SCRIPT_DEPS**
}

tasks.register('clean', Delete) {
    delete rootProject.layout.buildDirectory
}
";

        private const string GradleProperties = @"org.gradle.jvmargs=-Xmx**JVM_HEAP_SIZE**M
org.gradle.parallel=true
unityStreamingAssets=**STREAMING_ASSETS**
**ADDITIONAL_PROPERTIES**
";

        private const string LauncherTemplate = @"apply plugin: 'com.android.application'
apply from: 'setupSymbols.gradle'
apply from: '../shared/keepUnitySymbols.gradle'

dependencies {
    implementation project(':unityLibrary')
    }

android {
    namespace ""**NAMESPACE**""
    ndkPath ""**NDKPATH**""
    ndkVersion ""**NDKVERSION**""

    compileSdk **APIVERSION**
    buildToolsVersion = ""**BUILDTOOLS**""

    compileOptions {
        sourceCompatibility JavaVersion.VERSION_17
        targetCompatibility JavaVersion.VERSION_17
    }

    defaultConfig {
        minSdk **MINSDK**
        targetSdk **TARGETSDK**
        applicationId '**APPLICATIONID**'
        ndk {
            abiFilters **ABIFILTERS**
            debugSymbolLevel **DEBUGSYMBOLLEVEL**
        }
        versionCode **VERSIONCODE**
        versionName '**VERSIONNAME**'
    }

    androidResources {
        noCompress = **BUILTIN_NOCOMPRESS** + unityStreamingAssets.tokenize(', ')
        ignoreAssetsPattern = ""!.svn:!.git:!.ds_store:!*.scc:!CVS:!thumbs.db:!picasa.ini:!*~""
    }**SIGN**

    lint {
        abortOnError false
    }

    buildTypes {
        debug {
            minifyEnabled **MINIFY_DEBUG**
            proguardFiles getDefaultProguardFile('proguard-android.txt')**SIGNCONFIG**
            jniDebuggable true
        }
        release {
            minifyEnabled **MINIFY_RELEASE**
            proguardFiles getDefaultProguardFile('proguard-android.txt')**SIGNCONFIG**
        }
    }**PACKAGING****PLAY_ASSET_PACKS****SPLITS**
**BUILT_APK_LOCATION**
    bundle {
        language {
            enableSplit = false
        }
        density {
            enableSplit = false
        }
        abi {
            enableSplit = true
        }
        texture {
            enableSplit = true
        }
    }

	**GOOGLE_PLAY_DEPENDENCIES**
}**SPLITS_VERSION_CODE****LAUNCHER_SOURCE_BUILD_SETUP**
";

        private const string MainTemplate = @"apply plugin: 'com.android.library'
apply from: '../shared/keepUnitySymbols.gradle'
**APPLY_PLUGINS**

dependencies {
    implementation fileTree(dir: 'libs', include: ['*.jar'])
**DEPS**}

android {
    namespace ""com.unity3d.player""
    ndkPath ""**NDKPATH**""
    ndkVersion ""**NDKVERSION**""

    compileSdk **APIVERSION**
    buildToolsVersion = ""**BUILDTOOLS**""

    compileOptions {
        sourceCompatibility JavaVersion.VERSION_17
        targetCompatibility JavaVersion.VERSION_17
    }

    defaultConfig {
        minSdk **MINSDK**
        targetSdk **TARGETSDK**
        ndk {
            abiFilters **ABIFILTERS**
            debugSymbolLevel **DEBUGSYMBOLLEVEL**
        }
        versionCode **VERSIONCODE**
        versionName '**VERSIONNAME**'
        consumerProguardFiles 'proguard-unity.txt'**USER_PROGUARD**
**DEFAULT_CONFIG_SETUP**
    }

    lint {
        abortOnError false
    }

    androidResources {
        noCompress = **BUILTIN_NOCOMPRESS** + unityStreamingAssets.tokenize(', ')
        ignoreAssetsPattern = ""!.svn:!.git:!.ds_store:!*.scc:!CVS:!thumbs.db:!picasa.ini:!*~""
    }**PACKAGING**
}
**IL_CPP_BUILD_SETUP**
**SOURCE_BUILD_SETUP**
**EXTERNAL_SOURCES**
";

        private const string SettingsTemplate = @"pluginManagement {
    repositories {
        **ARTIFACTORYREPOSITORY**
        gradlePluginPortal()
        google()
        mavenCentral()
    }
}

include ':launcher', ':unityLibrary'
**INCLUDES**

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.PREFER_SETTINGS)
    repositories {
        **ARTIFACTORYREPOSITORY**
        google()
        mavenCentral()
        flatDir {
            dirs ""${project(':unityLibrary').projectDir}/libs""
        }
    }
}
";

    }
}
