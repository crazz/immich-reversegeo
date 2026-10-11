$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Release-Artifacts.ps1"
$script:passed = 0
function Assert-True([bool]$Condition, [string]$Message)
{
    if (!$Condition) { throw $Message }
    $script:passed++
}
function Assert-Rejected([scriptblock]$Action)
{
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert-True $rejected 'Expected validation failure.'
}
$sha = 'a' * 40
$metadata = New-ReleaseMetadata '0.0.0-dev' $sha 'refs/heads/candidate' 'branch' 'candidate'
Assert-True ($metadata.informationalVersion -ceq "0.0.0-dev+sha.$sha") 'Development metadata mismatch.'
$metadata = New-ReleaseMetadata '1.2.3-rc.1+build' $sha 'refs/tags/v1.2.3-rc.1+build' 'tag' 'v1.2.3-rc.1+build'
Assert-True ($metadata.informationalVersion -ceq "1.2.3-rc.1+build.sha.$sha") 'Prerelease metadata mismatch.'
foreach ($version in @('1.2', '01.2.3', '1.2.3-01', '1.2.3-rc.01', '1.2.3-', '1.2.3+', '1.2.3;echo bad', '65535.0.0'))
{
    Assert-Rejected { New-ReleaseMetadata $version $sha 'refs/heads/candidate' 'branch' 'candidate' }
}
Assert-Rejected { New-ReleaseMetadata '1.2.3' 'abc' 'refs/heads/candidate' 'branch' 'candidate' }
Assert-Rejected { New-ReleaseMetadata '1.2.4' $sha 'refs/tags/v1.2.3' 'tag' 'v1.2.3' }
$directory = Join-Path ([IO.Path]::GetTempPath()) ('reversegeo-release-tests-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $directory | Out-Null
function Write-FixtureZip([string]$Path, [hashtable]$Members)
{
    $archive = [IO.Compression.ZipArchive]::new([IO.File]::Create($Path), [IO.Compression.ZipArchiveMode]::Create)
    try
    {
        foreach ($name in $Members.Keys)
        {
            $stream = $archive.CreateEntry($name).Open()
            try
            {
                $bytes = [Text.Encoding]::UTF8.GetBytes($Members[$name])
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
try
{
    $metadata = New-ReleaseMetadata '1.2.3' $sha 'refs/heads/candidate' 'branch' 'candidate'
    $app = @{
        'ImmichReverseGeo.Web.dll' = 'assembly'; 'ImmichReverseGeo.Web.deps.json' = '{}'
        'ImmichReverseGeo.Web.runtimeconfig.json' = '{}'; 'ImmichReverseGeo.Web.staticwebassets.endpoints.json' = '{}'
        'release-build.json' = ($metadata | ConvertTo-Json)
        'bundled-data/iso3166.json' = '{}'; 'bundled-data/defaults/city-resolver-profiles.json' = '{}'
        'bundled-data/defaults/OVERTURE-ATTRIBUTION.txt' = 'Overture'
        'bundled-data/defaults/overture-airports.db' = "SQLite format 3`0fixture"
        'bundled-data/defaults/overture-country-divisions.db' = "SQLite format 3`0fixture"
    }
    $docs = @{ 'index.html' = 'home'; 'installation/index.html' = 'install'; 'configuration/index.html' = 'config'; 'llms.txt' = 'docs'; 'installation/index.md' = 'install' }
    $appPath = Join-Path $directory 'app.zip'
    $docsPath = Join-Path $directory 'docs.zip'
    $output = Join-Path $directory 'verification'
    Write-FixtureZip $appPath $app
    Write-FixtureZip $docsPath $docs
    Test-ReleasePackages $metadata $appPath $docsPath $output
    $manifest = Get-Content (Join-Path $output 'release-manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($manifest.sourceCommit -ceq $sha -and $manifest.artifacts.Count -eq 2) 'Manifest provenance mismatch.'
    Assert-True ($manifest.artifacts[0].sha256 -ceq (Get-FileHash $appPath).Hash.ToLowerInvariant()) 'Checksum mismatch.'
    $app['release-build.json'] = '{"sourceCommit":"stale"}'
    Write-FixtureZip $appPath $app
    Assert-Rejected { Test-ReleasePackages $metadata $appPath $docsPath $output }
    $app['release-build.json'] = $metadata | ConvertTo-Json
    $app['bundled-data/defaults/overture-airports.db'] = 'version https://git-lfs.github.com/spec/v1'
    Write-FixtureZip $appPath $app
    Assert-Rejected { Test-ReleasePackages $metadata $appPath $docsPath $output }
    $app['bundled-data/defaults/overture-airports.db'] = "SQLite format 3`0fixture"
    $app.Remove('bundled-data/iso3166.json')
    Write-FixtureZip $appPath $app
    Assert-Rejected { Test-ReleasePackages $metadata $appPath $docsPath $output }
    foreach ($name in @('../outside', 'C:/outside', 'folder\outside'))
    {
        Write-FixtureZip $appPath @{ $name = 'unsafe' }
        Assert-Rejected { Test-ReleaseArchive $appPath @() }
    }
    Write-FixtureZip $appPath @{ 'index.html' = '' }
    Assert-Rejected { Test-ReleaseArchive $appPath @('index.html') }
    Write-Host "$script:passed release verification assertions passed."
}
finally
{
    $resolved = [IO.Path]::GetFullPath($directory)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'reversegeo-release-tests-*')
    {
        throw 'Unexpected test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
