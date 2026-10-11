[CmdletBinding()]
param(
    [ValidateSet('Test', 'Start', 'Stop', 'Status')]
    [string] $Action = 'Test',
    [ValidateSet('All', 'v31', 'v32', 'v331')]
    [string] $Target = 'All'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$gitCommand = if ($IsWindows) { 'git.exe' } else { 'git' }
$commonGitDir = (& $gitCommand -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the primary checkout.' }
$primaryRoot = Split-Path -Parent $commonGitDir
$state = Join-Path $primaryRoot '.local/immich-db'
$stacks = @(
    @{ Name = 'v31'; Version = 'v3.1.0'; Port = 15283; DbPort = 15431 },
    @{ Name = 'v32'; Version = 'v3.2.0'; Port = 16283; DbPort = 16432 },
    @{ Name = 'v331'; Version = 'v3.3.1'; Port = 17283; DbPort = 17433 }
) | Where-Object { $Target -eq 'All' -or $_.Name -eq $Target }
& docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Start Docker before running this harness.' }
$endpoint = (& docker context inspect --format '{{.Endpoints.docker.Host}}').Trim()
if ($LASTEXITCODE -ne 0 -or $endpoint -notmatch '^(npipe|unix)://') { throw 'Use a local Docker context; remote endpoints are not accepted.' }
New-Item -ItemType Directory -Force -Path $state | Out-Null
[IO.File]::WriteAllText((Join-Path $state '.gitignore'), "*`n")
$previousEnvironment = @{}
foreach ($key in @('IMMICH_VERSION', 'IMMICH_PORT', 'DB_PORT', 'DB_PASSWORD', 'FIXTURE_GUARD')) {
    $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
$lease = [IO.File]::Open((Join-Path $state 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    if ($Action -in 'Start', 'Test') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compose.yml') -Destination (Join-Path $state 'compose.yml') -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'immich.json') -Destination (Join-Path $state 'immich.json') -Force
        & dotnet build (Join-Path $PSScriptRoot 'LiveImmichDb.csproj') --configuration Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Database fixture runner build failed.' }
    }
    foreach ($stack in $stacks) {
        $projectName = "immich-reversegeo-db-$($stack.Name)"
        $envFile = Join-Path $state "$($stack.Name).env"
        if (!(Test-Path -LiteralPath $envFile)) {
            if ($Action -in 'Stop', 'Status') { continue }
            $password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
            $environment = "IMMICH_VERSION=$($stack.Version)`nIMMICH_PORT=$($stack.Port)`nDB_PORT=$($stack.DbPort)`nDB_PASSWORD=$password`nFIXTURE_GUARD=$($stack.Name)-guard.sql`n"
            [IO.File]::WriteAllText($envFile, $environment)
        }
        # Guard against manually edited files redirecting the fixed, synthetic test targets.
        $environment = [IO.File]::ReadAllText($envFile)
        foreach ($pair in @(@{Key='IMMICH_VERSION'; Value=$stack.Version}, @{Key='IMMICH_PORT'; Value=$stack.Port}, @{Key='DB_PORT'; Value=$stack.DbPort}, @{Key='FIXTURE_GUARD'; Value="$($stack.Name)-guard.sql"})) {
            if ($environment -notmatch "(?m)^$($pair.Key)=$([regex]::Escape([string]$pair.Value))\r?$") { throw "Unexpected $($pair.Key) in fixture environment." }
        }
        foreach ($line in $environment.Split("`n")) {
            if ($line -match '^(IMMICH_VERSION|IMMICH_PORT|DB_PORT|DB_PASSWORD|FIXTURE_GUARD)=(.*)$') {
                [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2].TrimEnd("`r"))
            }
        }
        $guard = "CREATE SCHEMA immich_reversegeo_fixture;`nCREATE TABLE immich_reversegeo_fixture.guard (name text PRIMARY KEY);`nINSERT INTO immich_reversegeo_fixture.guard VALUES ('$projectName');`n"
        [IO.File]::WriteAllText((Join-Path $state "$($stack.Name)-guard.sql"), $guard)
        $composeArgs = @('compose', '-p', $projectName, '--env-file', $envFile, '-f', (Join-Path $state 'compose.yml'))
        if ($Action -eq 'Status') {
            & docker @composeArgs ps --all
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect fixture stack.' }
            continue
        }
        if ($Action -eq 'Stop') {
            & docker @composeArgs stop
            if ($LASTEXITCODE -ne 0) { throw 'Cannot stop fixture stack.' }
            continue
        }
        try {
            & docker @composeArgs up -d
            if ($LASTEXITCODE -ne 0) { throw "Could not start $projectName." }
            $deadline = [DateTime]::UtcNow.AddMinutes(3)
            do {
                $ready = $false
                try {
                    $version = Invoke-RestMethod -Uri "http://127.0.0.1:$($stack.Port)/api/server/version" -TimeoutSec 3
                    $actual = "v$($version.major).$($version.minor).$($version.patch)"
                    $ready = $actual -eq $stack.Version
                } catch { }
                if (!$ready) {
                    if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for $projectName; inspect its Compose logs." }
                    Start-Sleep -Seconds 2
                }
            } while (!$ready)
            if ($Action -eq 'Test') {
                & dotnet run --project (Join-Path $PSScriptRoot 'LiveImmichDb.csproj') --configuration Release --no-build -- $stack.Name $state
                if ($LASTEXITCODE -ne 0) { throw "Database checks failed for $($stack.Version)." }
            }
            Write-Host "$($stack.Version): $Action complete; fixture state: $state"
        }
        finally {
            # Test targets run sequentially and stop after checks; persistent volumes remain.
            if ($Action -eq 'Test') {
                & docker @composeArgs stop | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Warning "Could not stop $projectName; use -Action Stop." }
            }
        }
    }
}
finally {
    $lease.Dispose()
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key])
    }
}
