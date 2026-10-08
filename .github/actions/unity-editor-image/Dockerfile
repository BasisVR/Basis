ARG hubImage=unityci/hub:ubuntu-3.2.2
FROM $hubImage

ARG version
ARG changeSet
ARG modules

RUN unity-hub install --version "$version" --changeset "$changeSet" | tee /var/log/install-editor.log \
 && ! grep -q 'Failed to install\|Error while installing an editor\|Completed with errors' /var/log/install-editor.log \
 && test -x "$UNITY_PATH/editors/$version/Editor/Unity" \
 && for module in $modules; do \
      unity-hub install-modules --version "$version" --module "$module" --childModules | tee "/var/log/install-module-$module.log"; \
      jq -e --arg id "$module" 'any(.[]; .id == $id and .selected)' "$UNITY_PATH/editors/$version/modules.json" > /dev/null \
        || { echo "Unity module $module was not installed"; exit 1; }; \
    done \
 && mv "$UNITY_PATH/editors/$version"/* "$UNITY_PATH/" \
 && rm -rf "$UNITY_PATH/editors" /tmp/* /root/.cache \
 && chmod -R 755 "$UNITY_PATH/Editor/Data/PlaybackEngines" \
 && echo "$version" > "$UNITY_PATH/version"

RUN if ls -d "$UNITY_PATH"/Editor/Data/PlaybackEngines/LinuxStandaloneSupport/Variations/*il2cpp > /dev/null 2>&1; then \
      apt-get -q update \
      && apt-get -q install -y --no-install-recommends build-essential clang \
      && apt-get clean \
      && rm -rf /var/lib/apt/lists/*; \
    fi

RUN printf '%s\n' '#!/bin/bash' \
      'for i in /usr/bin/unity-editor.d/*.sh; do [ -r "$i" ] && . "$i"; done' \
      'xvfb-run -ae /dev/stdout "$UNITY_PATH/Editor/Unity" -batchmode "$@"' > /usr/bin/unity-editor \
 && chmod 755 /usr/bin/unity-editor \
 && mkdir -p /usr/bin/unity-editor.d

RUN set -e; \
    if [ -d "$UNITY_PATH/Editor/Data/PlaybackEngines/AndroidPlayer" ]; then \
      modulePath() { jq -r --arg id "$1" --arg key "$2" 'first(.[] | select(.id | startswith($id)) | .[$key] // empty)' "$UNITY_PATH/modules.json" | sed "s#{UNITY_PATH}#$UNITY_PATH#"; }; \
      sdk=$(modulePath android-sdk-platform-tools destination); \
      ndk=$(modulePath android-ndk destination); \
      jdk=$(modulePath android-open-jdk destination); \
      tools=$(modulePath android-sdk-command-line-tools renameTo); \
      printf 'export %s\n' "ANDROID_SDK_ROOT=$sdk" "ANDROID_HOME=$sdk" "ANDROID_NDK_HOME=$ndk" "JAVA_HOME=$jdk" "ANDROID_CMDLINE_TOOLS_PATH=$tools" "PATH=$jdk/bin:$tools/bin:$PATH" > /usr/bin/unity-editor.d/android.sh; \
      export JAVA_HOME="$jdk"; \
      yes | "$tools/bin/sdkmanager" --licenses > /dev/null; \
      [ -d "$sdk/platform-tools" ] || "$tools/bin/sdkmanager" platform-tools; \
    fi
