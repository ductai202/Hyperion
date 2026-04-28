#!/bin/bash

# Configuration
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet
PORT=3005
N=1000000
C=500
R=1000000
THREADS=3
WORKERS=$(nproc)
IO_HANDLERS=$((WORKERS / 2))

# Paths
HYPERION_DIR="./src/Hyperion.Server"
HYPERION_EXE="$HYPERION_DIR/bin/Release/net10.0/Hyperion.Server"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

function await_port() {
    local port=$1
    echo "Waiting for port $port to open..."
    for i in {1..20}; do
        if nc -z localhost $port; then
            return 0
        fi
        sleep 0.5
    done
    return 1
}

function run_bench() {
    local label=$1
    echo -e "\n${BLUE}--- $label ---${NC}"
    
    # Run redis-benchmark and filter output
    redis-benchmark -h 127.0.0.1 -p $PORT -t set,get -c $C -n $N -r $R --threads $THREADS 2>/dev/null | awk '
    /^======/ { print "\n" $0 }
    /Summary:/ { inSummary=1; count=0; print "Summary:" }
    inSummary && count < 4 { 
        if ($0 !~ /Summary:/) {
            print "  " $0;
            count++
        }
    }
    '
}

echo -e "${GREEN}=== Building Hyperion (Release) ===${NC}"
dotnet build $HYPERION_DIR/Hyperion.Server.csproj -c Release -v quiet
if [ $? -ne 0 ]; then echo "Build failed"; exit 1; fi
echo "Build OK"

# 1. Official Redis
echo -e "\n=========================================="
echo "  Official Redis (Linux)"
echo "=========================================="
redis-server --port $PORT --daemonize yes --protected-mode no
if ! await_port $PORT; then echo "Redis failed to start"; exit 1; fi
run_bench "Official Redis, SET/GET, 1M reqs, $C clients, $THREADS threads"
redis-cli -p $PORT shutdown
sleep 2

# 2. Hyperion Single
echo -e "\n=========================================="
echo "  Hyperion Single-Thread Mode"
echo "=========================================="
$HYPERION_EXE --port $PORT --mode single --log warning &
SERVER_PID=$!
if ! await_port $PORT; then echo "Hyperion failed to start"; kill $SERVER_PID; exit 1; fi
run_bench "Hyperion Single, SET/GET, 1M reqs, $C clients, $THREADS threads"
kill $SERVER_PID
sleep 2

# 3. Hyperion Multi
echo -e "\n=========================================="
echo "  Hyperion Multi-Thread Mode"
echo "=========================================="
$HYPERION_EXE --port $PORT --mode multi --workers $WORKERS --io $IO_HANDLERS --log warning &
SERVER_PID=$!
if ! await_port $PORT; then echo "Hyperion failed to start"; kill $SERVER_PID; exit 1; fi
run_bench "Hyperion Multi, SET/GET, 1M reqs, $C clients, $THREADS threads"
kill $SERVER_PID

echo -e "\n${GREEN}All benchmarks done.${NC}"
