param(
    [int]$Port = 5000
)

$ErrorActionPreference = "Stop"

Write-Host "Searching for GeminiWebAgent process on port $Port..." -ForegroundColor Cyan

$connections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $connections) {
    Write-Host "[i] No active GeminiWebAgent service found listening on port $Port." -ForegroundColor Yellow
    exit 0
}

foreach ($conn in $connections) {
    $procId = $conn.OwningProcess
    try {
        $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($p) {
            Write-Host "Stopping process PID $procId ($($p.ProcessName))..." -ForegroundColor Yellow
            Stop-Process -Id $procId -Force
            Write-Host "[SUCCESS] Stopped PID $procId" -ForegroundColor Green
        }
    } catch {
        Write-Warning "Failed to stop process PID $($procId): $_"
    }
}

Write-Host "GeminiWebAgent gateway has been stopped." -ForegroundColor Green
