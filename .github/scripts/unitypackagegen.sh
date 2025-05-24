#!/bin/bash

# Just need this to build for worlds
PACKAGES="Packages/org.basisvr.generator.equals-3.2.0.tgz:
        Packages/org.basisvr.newtonsoft.json-13.0.3.tgz:
        Packages/org.basisvr.base128-1.2.2.tgz:
        Packages/org.basisvr.simplebase-4.0.2.tgz:
        Packages/org.basisvr.bouncycastle-2.5.0.tgz"
SUBFOLDERS="Packages/com.basis.sdk:
        Packages/com.basis.odinserializer:
        Packages/com.basis.server"

if [[ "$1" == "full" ]]; then

  echo "Producing FULL package"

  PACKAGES+=":Packages/com.valvesoftware.unity.openvr-1.2.1.tgz"

  # Need this for framework (But only framework)
  SUBFOLDERS+=":Packages/com.basis.framework:
              Packages/com.basis.settingsmanager:
              Packages/com.basis.gizmos:
               Packages/com.basis.console:
              Packages/com.basis.visualtrackers:
              Packages/com.basis.addressables:
              Packages/com.steam.steamvr:
              Packages/com.steam.steamaudio:
              Packages/com.naelstrof.jigglephysics:
              Packages/com.hecomi.ulipsync:
              Packages/com.xiph.rnnoise:
              Packages/com.avionblock.opussharp"
elif [[ "$1" == "sdk" ]]; then
  echo "Producing SDK package"
  # All things are already included.
else
  echo "Only full and sdk targets are specified."
  die
fi

cd Basis

rm -rf generate_unitypackage
mkdir -p generate_unitypackage

echo $SUBFOLDERS | tr : '\n' | while read ddv; do
    # WOW! This actually works to list files with spaces!!! 
    find $ddv -type f -name "*.meta" -print0 | while read -d $'\0' -r FV ; do
        #printf 'File found: %s\n' "$FV"
        ASSET=${FV:0:${#FV} - 5}
        GUID=$(cat "$FV" | grep guid: | cut -d' ' -f2 | cut -b-32 )
        mkdir -p generate_unitypackage/$GUID
        if [[ -f "$ASSET" ]]; then
            #echo "$ASSET" TO generate_unitypackage/$GUID/asset
            #echo ASSET COPY cp "$ASSET" generate_unitypackage/$GUID/asset
            cp "$ASSET" generate_unitypackage/$GUID/asset
        fi
        cp "$FV" generate_unitypackage/$GUID/asset.meta
        #GPNAME=$(echo ${ddv:0:${#ddv} - 4} | cut -d/ -f3-)
        FONLY=$(echo $FV | rev | cut -d. -f2- | rev)
        echo "${FONLY}"
        echo "${FONLY}" > generate_unitypackage/$GUID/pathname
    done
done

echo "Now, exporting .tgz's"

if [[ ! -z $PACKAGES ]]; then
    echo $PACKAGES | tr : '\n' | while read ddv; do
        rm -rf tmp
        mkdir tmp
        tar xzf $ddv -C tmp/
        find tmp -type f -name "*.meta" -print0 | while read -d $'\0' -r f ; do
            ASSET=${f:0:${#f} - 5}
            GUID=$(cat "$f" | grep guid: | cut -d' ' -f2 | cut -b-32 )
            mkdir -p generate_unitypackage/$GUID
            if [[ -f "$ASSET" ]]; then
                cp "$ASSET" generate_unitypackage/$GUID/asset
            fi
            cp $f generate_unitypackage/$GUID/asset.meta
            GPNAME=$(echo "${ddv:0:${#ddv} - 4}" | cut -d/ -f1-)
            FONLY=$(echo "$f" | rev | cut -d. -f2- | rev | cut -d/ -f3-)
            echo "${GPNAME}/${FONLY}" "$GUID"
            echo "${GPNAME}/${FONLY}" > generate_unitypackage/$GUID/pathname
        done
    done
fi

echo "Done exporting .tgz's"

#If you wanted to append...
#cp BasisClient.unitypackage BasisClient.tar.gz
#rm -rf BasisClient.tar
#gunzip BasisClient.tar.gz
#tar r --file ../BasisClient.tar .

cd generate_unitypackage
tar czf ../Basis.$1.unitypackage .
cd ..

rm generate_unitypackage -rf

