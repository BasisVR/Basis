#!/bin/bash

# Just need this to build for worlds
PACKAGES="Packages/org.basisvr.generator.equals-3.2.0.tgz:
        Packages/org.basisvr.newtonsoft.json-13.0.3.tgz:
        Packages/org.basisvr.base128-1.2.2.tgz:
        Packages/org.basisvr.simplebase-4.0.2.tgz:
        Packages/org.basisvr.bouncycastle-2.5.0.tgz"
SUBFOLDERS="Packages/com.basis.sdk:
        Packages/com.basis.openlipsync:
        Packages/UnityJigglePhysics-upm:
        Packages/org.basisvr.k4os.compression.lz4:
        Packages/com.basis.bundlemanagement:
        Packages/com.basis.server"

EXTRASUBFOLDER=""

EXTRASUBFOLDERS=""
MOREFILES=""


if [[ "$1" == "full" ]]; then

  echo "Producing FULL package"

  PACKAGES+=":Packages/com.valvesoftware.unity.openvr-1.2.1.tgz"

  # Framework + client-only additions on top of the SDK base.
  SUBFOLDERS+=":Packages/com.avionblock.opussharp:
              Packages/com.basis.addon.snapcontrols:
              Packages/com.basis.common:
              Packages/com.basis.eventdriver:
              Packages/com.basis.examples:
              Packages/com.basis.framework:
              Packages/com.basis.framework.editor:
              Packages/com.basis.gizmos:
              Packages/com.basis.integration.audiolink:
              Packages/com.basis.openvr:
              Packages/com.basis.openxr:
              Packages/com.basis.profilerintergration:
              Packages/com.basis.settings:
              Packages/com.basis.shim:
              Packages/com.basis.textmeshpro:
              Packages/com.basis.vehicles:
              Packages/com.basis.visualtrackers:
              Packages/com.cnlohr.cilbox:
              Packages/com.cqf.urpvolumetricfog:
              Packages/com.llealloo.audiolink:
              Packages/com.steam.steamaudio:
              Packages/com.steam.steamvr:
              Packages/com.unity.3rdpersondemo:
              Packages/com.unity.render-pipelines.universal:
              Packages/com.xiph.rnnoise:
              Packages/dev.hai-vr.basis.comms:
              Packages/nuget.meamod.dns:
              Packages/org.basisvr.k4os.compression.lz4:
              Assets/AddressableAssetsData:
              Assets/Basis:
              Assets/MetaXR:
              Assets/Oculus:
              Assets/Plugins:
              Assets/Resources:
              Assets/StreamingAssets:
              Assets/XR"

  EXTRASUBFOLDERS+="ProjectSettings"

  MOREFILES+="Packages/manifest.json:Packages/packages-lock.json"

elif [[ "$1" == "sdk" ]]; then
  echo "Producing SDK package"
  # All things are already included.

else
  echo "Only full, sdk, and creator targets are specified."
  die
  exit
fi

set -e

# Fail loudly if a listed Packages/ path doesn't exist
for ddv in $(echo "$SUBFOLDERS" | tr : '\n'); do
    ddv_trimmed=$(echo "$ddv" | tr -d '[:space:]')
    if [[ -n "$ddv_trimmed" && "$ddv_trimmed" == Packages/* && ! -d "Basis/$ddv_trimmed" ]]; then
        echo "ERROR: SUBFOLDERS lists '$ddv_trimmed' but Basis/$ddv_trimmed does not exist." >&2
        exit 1
    fi
done

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
        echo "${FONLY}" "${GUID}"
        echo "${FONLY}" > generate_unitypackage/$GUID/pathname
    done
done

# Tricky trick for exporting projectsettings
echo ${EXTRASUBFOLDERS} | tr : '\n' | while read ddv; do
    find $ddv -type f -name "*.asset" -print0 | while read -d $'\0' -r FV ; do
        #printf 'File found: %s\n' "$FV"
        ASSET=$FV
        GUID=$(echo "$FV" | md5sum | cut -d' ' -f1 | cut -b-32 )
        mkdir -p generate_unitypackage/$GUID
        if [[ -f "$ASSET" ]]; then
            #echo "$ASSET" TO generate_unitypackage/$GUID/asset
            #echo ASSET COPY cp "$ASSET" generate_unitypackage/$GUID/asset
            cp "$ASSET" generate_unitypackage/$GUID/asset
        fi

		echo "fileFormatVersion: 2" > generate_unitypackage/$GUID/asset.meta
		echo "guid: ${GUID}" >> generate_unitypackage/$GUID/asset.meta

        #GPNAME=$(echo ${ddv:0:${#ddv} - 4} | cut -d/ -f3-)
        FONLY=$(echo $FV | rev | cut -d. -f2- | rev)
        echo "${ASSET}" "${GUID}"
        echo "${ASSET}" > generate_unitypackage/$GUID/pathname
    done
done

echo "Adding extra files: " ${MOREFILES}

if [[ -n ${MOREFILES} ]]; then
	echo ${MOREFILES} | tr : '\n' | while read FV ; do
		#printf 'File found: %s\n' "$FV"
		ASSET=$FV
		GUID=$(echo "$FV" | md5sum | cut -d' ' -f1 | cut -b-32 )
		mkdir -p generate_unitypackage/$GUID
		if [[ -f "$ASSET" ]]; then
		    #echo "$ASSET" TO generate_unitypackage/$GUID/asset
		    #echo ASSET COPY cp "$ASSET" generate_unitypackage/$GUID/asset
		    cp "$ASSET" generate_unitypackage/$GUID/asset
		fi

		echo "fileFormatVersion: 2" > generate_unitypackage/$GUID/asset.meta
		echo "guid: ${GUID}" >> generate_unitypackage/$GUID/asset.meta

		#GPNAME=$(echo ${ddv:0:${#ddv} - 4} | cut -d/ -f3-)
		FONLY=$(echo $FV | rev | cut -d. -f2- | rev)
		echo "${ASSET}" "${GUID}"
		echo "${ASSET}" > generate_unitypackage/$GUID/pathname
	done
fi

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

