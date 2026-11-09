#!/bin/sh
set -e
root="$(cd "$(dirname "$0")" && pwd)"
config=release
flags=--release
if [ "$1" = "--debug" ]; then config=debug; flags=; fi
if [ -z "$BASIS_RT_DXC" ] && ! command -v dxc >/dev/null 2>&1 && [ ! -x "$root/.deps/dxc-linux/bin/dxc" ]; then
    echo "dxc was not found: install the Vulkan SDK or DirectXShaderCompiler, or set BASIS_RT_DXC" >&2
    exit 1
fi
cd "$root"
cargo build -p basis-rt-tracer -p basis-rt-bridge -p basis-rt-harness $flags
bin="$root/../Tracer"
plugins="$root/../../Plugins/x86_64"
mkdir -p "$bin" "$plugins"
pkill -f "$bin/BasisRayTracer" 2>/dev/null || true
cp "$root/target/$config/BasisRayTracer" "$bin/BasisRayTracer"
chmod 755 "$bin/BasisRayTracer"
cp "$root/target/$config/libBasisRayTracingBridge.so" "$plugins/libBasisRayTracingBridge2.so"
echo "Installed tracer to $bin and bridge to $plugins"
echo "Self test: $root/target/$config/basis-rt-harness $bin/BasisRayTracer /tmp/basis-rt.ppm 48"
