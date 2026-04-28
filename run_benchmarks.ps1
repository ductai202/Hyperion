$ErrorActionPreference = "Continue"

$REDIS_SERVER    = ".\redis-win\redis-server.exe"
$REDIS_BENCHMARK = ".\redis-win\redis-benchmark.exe"
$HYPERION_EXE    = ".\src\Hyperion.Server\bin\Release\net10.0\Hyperion.Server.exe"

# ---- Benchmark parameters ----
$N       = 1000000                       # 1M requests
$R       = 1000000                       # 1M key space
$C       = 500                           # 500 concurrent clients
$THREADS = 3                             # benchmark client threads

function Await-Port {
    param([int]$Port, [int]$TimeoutMs = 15000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $tcp.Connect("127.0.0.1", $Port)
            $tcp.Close()
            return $true
        } catch { Start-Sleep -Milliseconds 300 }
    }
    return $false
}

function Run-Bench {
    param([string]$Label)
    Write-Host ""
    Write-Host "--- $Label ---"

    $lines = & $REDIS_BENCHMARK `
        -h 127.0.0.1 -p 3000 `
        -t set,get `
        -c $C `
        -n $N `
        -r $R `
        --threads $THREADS `
        2>$null

    # Print only the clean Summary block for each command (SET and GET)
    $inSummary  = $false
    $rowsPrinted = 0

    foreach ($line in $lines) {
        $l = $line.ToString().Trim()

        if ($l -match "^====== .+ ======") {
            Write-Host ""
            Write-Host $l
            $inSummary  = $false
            $rowsPrinted = 0
            continue
        }
        if ($l -match "^Summary:") {
            $inSummary  = $true
            $rowsPrinted = 0
            Write-Host "Summary:"
            continue
        }
        # Print throughput + latency header + latency data row (4 rows total)
        if ($inSummary -and $rowsPrinted -lt 4) {
            Write-Host "  $l"
            $rowsPrinted++
        }
    }
    Write-Host ""
}

# -------- Build Release --------
Write-Host "=== Building Hyperion (Release) ==="
dotnet build .\src\Hyperion.Server\Hyperion.Server.csproj -c Release -v quiet
Write-Host "Build OK"

# -------- 1. Origin Redis --------
Write-Host ""
Write-Host "=========================================="
Write-Host "  Origin Redis 8.6.2"
Write-Host "=========================================="
$redisProc = Start-Process -FilePath $REDIS_SERVER -ArgumentList "--port 3000" -PassThru -WindowStyle Hidden
if (-not (Await-port 3000)) { Write-Error "Redis did not start"; exit 1 }
Run-Bench -Label "Origin Redis, SET/GET, 1M reqs, $C clients, $THREADS threads"
Stop-Process -Id $redisProc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# -------- 2. Hyperion Single Thread --------
Write-Host ""
Write-Host "=========================================="
Write-Host "  Hyperion Single-Thread Mode"
Write-Host "=========================================="
$hypSingle = Start-Process -FilePath $HYPERION_EXE -ArgumentList "--port 3000 --mode single --log warning" -PassThru -WindowStyle Hidden
if (-not (Await-port 3000 20000)) { Write-Error "Hyperion single did not start"; exit 1 }
Run-Bench -Label "Hyperion Single, SET/GET, 1M reqs, $C clients, $THREADS threads"
Stop-Process -Id $hypSingle.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# -------- 3. Hyperion Multi Thread --------
Write-Host ""
Write-Host "=========================================="
Write-Host "  Hyperion Multi-Thread Mode"
Write-Host "=========================================="
$hypMulti = Start-Process -FilePath $HYPERION_EXE -ArgumentList "--port 3000 --mode multi --workers 8 --io 4 --log warning" -PassThru -WindowStyle Hidden
if (-not (Await-port 3000 20000)) { Write-Error "Hyperion multi did not start"; exit 1 }
Run-Bench -Label "Hyperion Multi, SET/GET, 1M reqs, $C clients, $THREADS threads"
Stop-Process -Id $hypMulti.Id -Force -ErrorAction SilentlyContinue

Write-Host "All benchmarks done."
