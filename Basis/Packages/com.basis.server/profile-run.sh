#!/bin/bash
# Profiling script for Basis Server with 1000 clients
# This script:
#   1. Starts the server with BSR profiling + statistics enabled
#   2. Attaches dotnet-counters to the server for live metrics
#   3. Starts the client console with 1000 connections
#   4. After stabilization, captures a dotnet-trace CPU profile
#   5. Collects all data into a results directory

set -e

BASE="C:/Users/doola/OneDrive/Documents/Github/Basis/Basis Server"
SERVER_DIR="$BASE/BasisServerConsole/bin/Release/net10.0"
CLIENT_DIR="$BASE/BasisNetworkClientConsole/BasisNetworkClientConsole/bin/Release/net10.0"
RESULTS="$BASE/profiling-results"

mkdir -p "$RESULTS"
echo "=== Profiling results will be saved to: $RESULTS ==="

# Kill any leftover processes
taskkill //F //IM BasisNetworkConsole.exe 2>/dev/null || true
taskkill //F //IM BasisNetworkClientConsole.exe 2>/dev/null || true
sleep 2

echo ""
echo "=== STEP 1: Starting server with BSR profiling enabled ==="
export EnableBSRProfiling=True
export EnableStatistics=True
export EnableConsole=False
export UseAuthIdentity=False

cd "$SERVER_DIR"
./BasisNetworkConsole.exe &
SERVER_PID=$!
echo "Server PID: $SERVER_PID"
sleep 3

# Get the actual Windows PID for dotnet tools
SERVER_DOTNET_PID=$(dotnet-counters ps 2>/dev/null | grep -i "BasisNetworkConsole" | head -1 | awk '{print $1}')
echo "Server .NET PID: $SERVER_DOTNET_PID"

if [ -z "$SERVER_DOTNET_PID" ]; then
    echo "ERROR: Could not find server process. Check if it started correctly."
    exit 1
fi

echo ""
echo "=== STEP 2: Starting dotnet-counters monitoring (30s baseline) ==="
dotnet-counters collect \
    -p "$SERVER_DOTNET_PID" \
    --counters "System.Runtime,System.Net.Sockets,System.Threading" \
    --format csv \
    -o "$RESULTS/counters-baseline.csv" \
    --duration 00:00:15 &
COUNTERS_PID=$!

sleep 5

echo ""
echo "=== STEP 3: Starting 1000 client connections ==="
cd "$CLIENT_DIR"
./BasisNetworkClientConsole.exe &
CLIENT_PID=$!
echo "Client PID: $CLIENT_PID"

echo ""
echo "=== Waiting for connections to establish (120s) ==="
sleep 120

echo ""
echo "=== STEP 4: Collecting dotnet-counters under load ==="
# Wait for baseline collection to finish
wait $COUNTERS_PID 2>/dev/null || true

dotnet-counters collect \
    -p "$SERVER_DOTNET_PID" \
    --counters "System.Runtime,System.Net.Sockets,System.Threading" \
    --format csv \
    -o "$RESULTS/counters-underload.csv" \
    --duration 00:00:30 &
COUNTERS_LOAD_PID=$!

echo ""
echo "=== STEP 5: Capturing CPU trace (30 seconds) ==="
dotnet-trace collect \
    -p "$SERVER_DOTNET_PID" \
    --duration 00:00:30 \
    --format Speedscope \
    -o "$RESULTS/server-cpu-trace" &
TRACE_PID=$!

echo "Waiting for trace collection..."
wait $TRACE_PID 2>/dev/null || true
wait $COUNTERS_LOAD_PID 2>/dev/null || true

echo ""
echo "=== STEP 6: Collecting GC and thread info ==="
dotnet-counters monitor \
    -p "$SERVER_DOTNET_PID" \
    --counters "System.Runtime[gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,threadpool-thread-count,threadpool-queue-length,working-set,cpu-usage,alloc-rate,exception-count,time-in-gc]" \
    --duration 00:00:10 > "$RESULTS/gc-thread-snapshot.txt" 2>&1 || true

echo ""
echo "=== STEP 7: Cleanup ==="
taskkill //F //IM BasisNetworkClientConsole.exe 2>/dev/null || true
sleep 2
taskkill //F //IM BasisNetworkConsole.exe 2>/dev/null || true

echo ""
echo "=== PROFILING COMPLETE ==="
echo "Results saved to: $RESULTS"
ls -la "$RESULTS/"
