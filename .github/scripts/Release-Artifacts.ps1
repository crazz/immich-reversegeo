param(
    [ValidateSet('Metadata', 'Verify')][string]$Action,
    [string]$MetadataPath = '_out/release-metadata.json',
    [string]$AppPath = 'immich-reversegeo-web.zip',
    [string]$DocsPath = 'immich-reversegeo-docs.zip',
    [string]$OutputDirectory = '_out/release-verification'
)
$ErrorActionPreference = 'Stop'

function New-ReleaseMetadata
{
    param([string]$Version, [string]$Commit, [string]$SourceRef, [string]$RefType, [string]$RefName)
    $pattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
    $match = [regex]::Match($Version, $pattern)
    if (!$match.Success)
    {
        throw 'Project Version must be SemVer.'
    }
    foreach ($index in 1..3)
    {
        if ([decimal]$match.Groups[$index].Value -gt 65534)
        {
            throw 'Version components exceed .NET assembly limits.'
        }
    }
    foreach ($identifier in $match.Groups[4].Value.Split('.'))
    {
        if ($identifier -match '^0[0-9]+$')
        {
            throw 'Numeric prerelease identifiers cannot have leading zeros.'
        }
    }
    if ($Commit -cnotmatch '^[0-9a-f]{40}$')
    {
        throw 'Source commit must be a full lowercase Git SHA.'
    }
    if ($RefType -eq 'tag' -and $RefName -cne "v$Version" -and $RefName -cne $Version)
    {
        throw 'Release tag must match the project Version.'
    }
    $separator = if ($Version.Contains('+')) { '.' } else { '+' }
    return [ordered]@{
        version = $Version
        informationalVersion = "$Version${separator}sha.$Commit"
        sourceCommit = $Commit
        sourceRef = $SourceRef
    }
}

function Test-ReleaseArchive
{
    param([string]$Path, [string[]]$Required, $Metadata)
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    try
    {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries)
        {
            if (!$names.Add($entry.FullName) -or $entry.FullName -match '(^/|\\|(^|/)\.\.(/|$)|^[A-Za-z]:)')
            {
                throw "Unsafe or duplicate ZIP path: $($entry.FullName)"
            }
            # Read every member to detect truncated/compression failures.
            $stream = $entry.Open()
            try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() }
        }
        foreach ($name in $Required)
        {
            $entry = $archive.GetEntry($name)
            if (!$entry -or $entry.Length -eq 0)
            {
                throw "Missing or empty $name in $Path"
            }
            if ($name.EndsWith('.db'))
            {
                $stream = $entry.Open()
                try
                {
                    $header = [IO.BinaryReader]::new($stream).ReadBytes(16)
                    if ($header.Length -ne 16 -or [Text.Encoding]::ASCII.GetString($header) -cne "SQLite format 3`0")
                    {
                        throw "Bundled database $name is not SQLite; check Git LFS hydration."
                    }
                }
                finally { $stream.Dispose() }
            }
        }
        if ($null -ne $Metadata)
        {
            $reader = [IO.StreamReader]::new($archive.GetEntry('release-build.json').Open())
            try { $embedded = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            foreach ($key in @('version', 'informationalVersion', 'sourceCommit', 'sourceRef'))
            {
                if ($embedded.$key -cne $Metadata.$key)
                {
                    throw "Packaged $key does not match the candidate."
                }
            }
        }
    }
    finally { $archive.Dispose() }
}

function Test-ReleasePackages
{
    param($Metadata, [string]$AppPath, [string]$DocsPath, [string]$OutputDirectory)
    $appFiles = @('ImmichReverseGeo.Web.dll', 'ImmichReverseGeo.Web.deps.json',
        'ImmichReverseGeo.Web.runtimeconfig.json', 'ImmichReverseGeo.Web.staticwebassets.endpoints.json',
        'release-build.json', 'bundled-data/iso3166.json', 'bundled-data/defaults/city-resolver-profiles.json',
        'bundled-data/defaults/OVERTURE-ATTRIBUTION.txt', 'bundled-data/defaults/overture-airports.db',
        'bundled-data/defaults/overture-country-divisions.db')
    Test-ReleaseArchive $AppPath $appFiles $Metadata
    Test-ReleaseArchive $DocsPath @('index.html', 'installation/index.html', 'configuration/index.html', 'llms.txt', 'installation/index.md')
    $artifacts = @(foreach ($path in @($AppPath, $DocsPath))
    {
        $file = Get-Item -LiteralPath $path
        [ordered]@{ name = $file.Name; sizeBytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $manifest = [ordered]@{}
    foreach ($key in @('version', 'informationalVersion', 'sourceCommit', 'sourceRef'))
    {
        $manifest[$key] = $Metadata.$key
    }
    $manifest.artifacts = $artifacts
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-manifest.json') -Encoding utf8
    $artifacts | ForEach-Object { "$($_.sha256)  $($_.name)" } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS') -Encoding utf8
    $artifacts | ForEach-Object { Write-Host "Verified $($_.name): $($_.sizeBytes) bytes; SHA256 $($_.sha256)" }
}

if ($MyInvocation.InvocationName -ne '.')
{
    if ($Action -eq 'Metadata')
    {
        $metadata = New-ReleaseMetadata $env:PROJECT_VERSION $env:SOURCE_COMMIT $env:SOURCE_REF $env:RELEASE_REF_TYPE $env:RELEASE_REF_NAME
        New-Item -ItemType Directory -Force -Path (Split-Path $MetadataPath) | Out-Null
        $metadata | ConvertTo-Json | Set-Content -LiteralPath $MetadataPath -Encoding utf8
        if ($env:GITHUB_OUTPUT)
        {
            "informational_version=$($metadata.informationalVersion)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
        }
        Write-Host "Candidate version: $($metadata.version); source: $($metadata.sourceCommit)"
    }
    elseif ($Action -eq 'Verify')
    {
        Test-ReleasePackages (Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json) $AppPath $DocsPath $OutputDirectory
    }
    else
    {
        throw 'Specify -Action Metadata or Verify.'
    }
}
