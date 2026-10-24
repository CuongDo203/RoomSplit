<#
.SYNOPSIS
    Measures GET /api/v1/rooms/{roomId}/members response time without cache, on a cache miss and on a cache hit.

.DESCRIPTION
    Runs against the dev docker compose stack (docker-compose.yml + docker-compose.override.yml).
    - No cache:   the api container is recreated with CACHE_ENABLED=false (Cache__Enabled=false).
    - Cache miss: cache on; the Redis key is deleted before every request (deletion is not timed).
    - Cache hit:  cache on; the key is already in Redis.
    Each scenario starts with one untimed warm-up request. Times are curl's time_total (client side,
    including the TCP connection). The api container is left with the cache on.

.EXAMPLE
    ./scripts/measure-members-cache.ps1 -RoomId 3 -Runs 10
#>
param(
    [int] $RoomId = 3,
    [ValidateRange(5, 1000)] [int] $Runs = 10,
    [string] $BaseUrl = "http://localhost:8080",
    [string] $RedisContainer = "roomsplit-redis"
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

$url = "$BaseUrl/api/v1/rooms/$RoomId/members"
$redisKey = "roomsplit:members:room:$RoomId"

function Invoke-Timed {
    $out = curl.exe -s -o NUL -w "%{http_code} %{time_total}" $url
    $code, $seconds = $out -split " "
    if ($code -ne "200") { throw "GET $url returned HTTP $code" }
    [math]::Round([double]::Parse($seconds, [cultureinfo]::InvariantCulture) * 1000, 2)
}

function Restart-Api([bool] $cacheEnabled) {
    $env:CACHE_ENABLED = $cacheEnabled.ToString().ToLowerInvariant()
    docker compose up -d api 2>&1 | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        $code = curl.exe -s -o NUL -w "%{http_code}" "$BaseUrl/health/ready"
        if ($code -eq "200") { return }
        Start-Sleep -Seconds 1
    }
    throw "API did not become ready"
}

function Get-RedisKeyExists { (docker exec $RedisContainer redis-cli EXISTS $redisKey).Trim() }

function Measure-Scenario([string] $name, [scriptblock] $beforeEach) {
    & $beforeEach
    Invoke-Timed | Out-Null   # warm-up (JIT, connection pools), not recorded
    $times = foreach ($i in 1..$Runs) {
        & $beforeEach
        Invoke-Timed
    }
    $sorted = $times | Sort-Object
    $mid = [int][math]::Floor($Runs / 2)
    $median = if ($Runs % 2) { $sorted[$mid] } else { ($sorted[$mid - 1] + $sorted[$mid]) / 2 }
    [pscustomobject]@{
        Scenario = $name
        Runs     = ($times | ForEach-Object { $_.ToString("0.00", [cultureinfo]::InvariantCulture) }) -join ", "
        MinMs    = [math]::Round(($sorted | Select-Object -First 1), 2)
        AvgMs    = [math]::Round(($times | Measure-Object -Average).Average, 2)
        MedianMs = [math]::Round($median, 2)
        MaxMs    = [math]::Round(($sorted | Select-Object -Last 1), 2)
    }
}

Write-Host "GET $url, $Runs runs per scenario"

Write-Host "1/3 No cache (recreating api with CACHE_ENABLED=false)..."
Restart-Api $false
docker exec $RedisContainer redis-cli DEL $redisKey | Out-Null   # leftover from earlier requests
$noCache = Measure-Scenario "No cache" {}
if ((Get-RedisKeyExists) -ne "0") { throw "Key $redisKey exists although the cache is off" }

Write-Host "2/3 Cache miss (recreating api with CACHE_ENABLED=true)..."
Restart-Api $true
$miss = Measure-Scenario "Cache miss" { docker exec $RedisContainer redis-cli DEL $redisKey | Out-Null }

Write-Host "3/3 Cache hit..."
$hit = Measure-Scenario "Cache hit" {}
if ((Get-RedisKeyExists) -ne "1") { throw "Key $redisKey is missing after the hit scenario" }

Remove-Item Env:CACHE_ENABLED

$results = @($noCache, $miss, $hit)
$results | Format-Table Scenario, MinMs, AvgMs, MedianMs, MaxMs -AutoSize
$results | ForEach-Object { Write-Host ("{0}: {1}" -f $_.Scenario, $_.Runs) }

# Markdown rows, ready to paste into docs/redis-members-cache.md
Write-Host ""
Write-Host "| Kịch bản | Min (ms) | Trung bình (ms) | Trung vị (ms) | Max (ms) |"
Write-Host "|---|---|---|---|---|"
$results | ForEach-Object { Write-Host "| $($_.Scenario) | $($_.MinMs) | $($_.AvgMs) | $($_.MedianMs) | $($_.MaxMs) |" }
