param(
    [string]$BaseUrl = "http://127.0.0.1:5000/v1"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   Running GeminiWebAgent .NET 10 Verification Suite      " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Target Base URL: $BaseUrl" -ForegroundColor Gray

# Step 1: Check Gateway Health Endpoint
$healthUrl = "$BaseUrl/health"
Write-Host "`n[Step 1/2] Checking Gateway Health Endpoint ($healthUrl)..." -ForegroundColor Yellow
try {
    $healthResp = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 5
    Write-Host "Gateway status: $($healthResp.status)" -ForegroundColor Green
} catch {
    Write-Warning "Could not connect to gateway at $healthUrl. Running offline test suite only."
}

# Step 2: Run .NET 10 xUnit Test Project
Write-Host "`n[Step 2/2] Running .NET 10 Test Project (GeminiWebAgent.Tests)..." -ForegroundColor Yellow

$testProj = (Resolve-Path (Join-Path $PSScriptRoot "..\tests\GeminiWebAgent.Tests\GeminiWebAgent.Tests.csproj")).Path

Write-Host "Compiling test suite with .NET 10 SDK..." -ForegroundColor Gray
dotnet build "$testProj" --no-dependencies
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test project build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "Executing xUnit test runner..." -ForegroundColor Gray
dotnet test "$testProj" --no-build --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test execution failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "   ALL .NET 10 TESTS PASSED SUCCESSFULLY! (100% PASS)    " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
