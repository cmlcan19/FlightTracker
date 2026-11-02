# FlightTracker smoke test - read-only checks for Compose, API, PostgreSQL and logs.
# Place this file in the FlightTracker project root and run:
#   powershell -ExecutionPolicy Bypass -File .\FlightTracker_smoke_test.ps1
$ErrorActionPreference = 'Stop'

function Show-Result([string]$Title, [scriptblock]$Action) {
    Write-Host "`n==== $Title ====" -ForegroundColor Cyan
    try { & $Action }
    catch { Write-Host "HATA: $($_.Exception.Message)" -ForegroundColor Red }
}

Show-Result 'Docker Compose config' {
    docker compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'compose.yaml kontrolu basarisiz' }
    Write-Host 'Compose konfigurasyonu: OK' -ForegroundColor Green
}

Show-Result 'Containers' { docker compose ps }

Show-Result 'API / latest (all)' {
    $data = Invoke-RestMethod 'http://localhost:5050/api/flights/latest'
    $count = if ($null -eq $data) { 0 } elseif ($data -is [Array]) { $data.Length } else { 1 }
    Write-Host "Kayitli ucak: $count"
}

Show-Result 'API / latest (recent)' {
    $data = Invoke-RestMethod 'http://localhost:5050/api/flights/latest?onlyRecent=true'
    $count = if ($null -eq $data) { 0 } elseif ($data -is [Array]) { $data.Length } else { 1 }
    Write-Host "Son 10 dakikadaki ucak: $count (0 normal olabilir)"
}

Show-Result 'API / archive Oct 14 2026' {
    $data = Invoke-RestMethod 'http://localhost:5050/api/flights/archive?fromUtc=2026-10-13T21%3A00%3A00Z&toUtc=2026-10-14T21%3A00%3A00Z&limit=1000'
    $count = if ($null -eq $data) { 0 } elseif ($data -is [Array]) { $data.Length } else { 1 }
    Write-Host "14 Ekim arşiv uçak sayısı: $count"
}

Show-Result 'API / sessions (4bc8ce)' {
    $data = Invoke-RestMethod 'http://localhost:5050/api/flights/4bc8ce/sessions?limit=500'
    $count = if ($null -eq $data) { 0 } elseif ($data -is [Array]) { $data.Length } else { 1 }
    Write-Host "Ucus oturumlari: $count"
}

Show-Result 'PostgreSQL counts and freshness' {
    docker compose exec -T postgres psql -U flighttracker -d flighttracker -c "SELECT COUNT(*) AS observations, COUNT(DISTINCT icao24) AS aircraft, MAX(received_at_utc) AS last_received FROM flight_observations;"
}

Show-Result 'Producer recent log' { docker compose logs --tail=12 producer }
Show-Result 'Consumer recent log' { docker compose logs --tail=12 consumer }

Write-Host "`nSmoke test tamamlandi. HATA satirlarini ve servis loglarini kontrol edin." -ForegroundColor Green
