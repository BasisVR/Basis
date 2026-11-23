#!/usr/bin/env bash
set -u
mode=$1
probe=$(realpath "$2")
out=$(realpath -m "$3")
seconds=${4:-20}
mkdir -p "$out"

if [ -z "${INSIDE_SESSION:-}" ]; then
    export XDG_RUNTIME_DIR=$(mktemp -d)
    chmod 700 "$XDG_RUNTIME_DIR"
    export XDG_CURRENT_DESKTOP=sway XDG_SESSION_TYPE=wayland INSIDE_SESSION=1
    mkdir -p ~/.config/sway ~/.config/xdg-desktop-portal-wlr ~/.config/xdg-desktop-portal
    cat > ~/.config/sway/config <<'EOF'
output HEADLESS-1 mode 1280x720
xwayland force
exec foot sh -c 'while true; do date +%s.%N; sleep 0.02; done'
EOF
    cat > ~/.config/xdg-desktop-portal-wlr/config <<'EOF'
[screencast]
chooser_type=none
output_name=HEADLESS-1
EOF
    cat > ~/.config/xdg-desktop-portal/sway-portals.conf <<'EOF'
[preferred]
default=wlr;gtk
org.freedesktop.impl.portal.ScreenCast=wlr
EOF
    exec dbus-run-session -- bash "$0" "$@"
fi

pids=()
cleanup() {
    for pid in "${pids[@]}"; do
        kill "$pid" 2>/dev/null
    done
}
trap cleanup EXIT

pipewire > "$out/pipewire.log" 2>&1 & pids+=($!)
sleep 1
wireplumber > "$out/wireplumber.log" 2>&1 & pids+=($!)
sleep 1
pw-cli create-node adapter '{ factory.name=support.null-audio-sink node.name=probe-sink media.class=Audio/Sink audio.position=[ FL FR ] object.linger=true }' > /dev/null 2>&1
WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 sway > "$out/sway.log" 2>&1 & pids+=($!)
for _ in $(seq 50); do
    [ -S "$XDG_RUNTIME_DIR/wayland-1" ] && break
    sleep 0.2
done
export WAYLAND_DISPLAY=wayland-1
for _ in $(seq 50); do
    [ -S /tmp/.X11-unix/X0 ] && break
    sleep 0.2
done
export DISPLAY=:0
echo "xwayland: $( [ -S /tmp/.X11-unix/X0 ] && echo ok || echo missing)"
dbus-update-activation-environment DISPLAY WAYLAND_DISPLAY XDG_CURRENT_DESKTOP XDG_SESSION_TYPE XDG_RUNTIME_DIR
libexec() {
    for dir in /usr/libexec /usr/lib /usr/lib/xdg-desktop-portal; do
        [ -x "$dir/$1" ] && { echo "$dir/$1"; return; }
    done
    echo "$1"
}
for _ in $(seq 10); do
    grim "$out/grim.png" 2> "$out/grim.txt" && break
    sleep 0.5
done
echo "grim: $( [ -s "$out/grim.png" ] && echo ok || cat "$out/grim.txt")"
"$(libexec xdg-desktop-portal-wlr)" -l DEBUG > "$out/xdpw.log" 2>&1 & pids+=($!)
sleep 1
"$(libexec xdg-desktop-portal)" -v > "$out/xdp.log" 2>&1 & pids+=($!)
sleep 2

winpath="Z:${out}/probe"
status=0
case "$mode" in
    wine)
        export WINEPREFIX="$out/prefix" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree=;mshtml="
        wineboot -i > "$out/wineboot.log" 2>&1
        timeout $((seconds + 90)) wine "$probe" "$winpath" "$seconds" > "$out/run.log" 2>&1 || status=$?
        ;;
    proton)
        export WINEPREFIX="$out/prefix" GAMEID=umu-0 PROTONPATH=${PROTONPATH:-GE-Proton} UMU_LOG=debug
        timeout $((seconds + 900)) "$UMU_RUN" "$probe" "$winpath" "$seconds" > "$out/run.log" 2>&1 || status=$?
        ;;
    *)
        echo "mode must be wine or proton" >&2
        exit 2
        ;;
esac

echo "probe exit $status"
grep -E "RESULT|PASS|FAIL|capture helper|error" "$out/probe.log" "$out/run.log" 2>/dev/null | tail -40
if [ -s "$out/probe.h264" ]; then
    ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=codec_name,width,height,nb_read_frames -of default=nw=1 "$out/probe.h264" | tee "$out/ffprobe.txt"
    ffmpeg -v error -i "$out/probe.h264" -f null - > "$out/decode-errors.txt" 2>&1
    ffmpeg -v error -i "$out/probe.h264" -vf "select='not(mod(n\,60))'" -fps_mode vfr "$out/frame-%02d.png"
    echo "decode errors: $(wc -l < "$out/decode-errors.txt")"
fi
exit "$status"
