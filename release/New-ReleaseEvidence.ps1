[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ArtifactDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string] $PackageVersion,

    [string] $ComponentPath,

    [string] $SbomToolPath = 'sbom-tool',

    [Parameter(Mandatory)]
    [datetime] $GenerationTimestamp,

    [switch] $RequireSignature
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$temporaryComponentRoot = $null
try {
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory -ErrorAction Stop).Path
$artifactLeaf = Split-Path -Leaf $artifactRoot
$artifactParentLeaf = Split-Path -Leaf (Split-Path -Parent $artifactRoot)
$artifactName = "$artifactParentLeaf-$artifactLeaf"
$checksumPath = Join-Path $artifactRoot 'SHA256SUMS.txt'
$manifestRoot = Join-Path $artifactRoot '_manifest'

$sbomCommand = Get-Command $SbomToolPath -ErrorAction SilentlyContinue
if ($null -eq $sbomCommand) {
    throw "sbom-tool was not found. Install it with: dotnet tool install --global Microsoft.Sbom.DotNetTool --version 4.1.5"
}

if ([string]::IsNullOrWhiteSpace($ComponentPath)) {
    $architecture = switch ($artifactLeaf.ToLowerInvariant()) {
        'win-x64' { 'x64' }
        'win-arm64' { 'arm64' }
        default { throw "Cannot infer architecture from artifact directory '$artifactLeaf'. Pass -ComponentPath explicitly." }
    }
    $publishProfile = switch ($artifactParentLeaf.ToLowerInvariant()) {
        'framework-dependent' { "FrameworkDependent-$architecture" }
        'self-contained' { "SelfContained-$architecture" }
        default { throw "Cannot infer publish profile from artifact directory '$artifactParentLeaf'. Pass -ComponentPath explicitly." }
    }

    $projectPath = (Resolve-Path -LiteralPath (
        Join-Path $PSScriptRoot '..\TempMonitor\TempMonitor.csproj'
    ) -ErrorAction Stop).Path
    $temporaryComponentRoot = Join-Path (
        [System.IO.Path]::GetTempPath()
    ) "gxTempMonitor-sbom-components-$PID-$([guid]::NewGuid().ToString('N'))"
    [System.IO.Directory]::CreateDirectory($temporaryComponentRoot) | Out-Null

    & dotnet restore $projectPath `
        -p:PublishProfile=$publishProfile `
        "-p:BaseIntermediateOutputPath=$temporaryComponentRoot\" `
        -p:NuGetAudit=true `
        -p:NuGetAuditMode=all `
        -p:WarningsAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904 `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Isolated component restore failed for profile $publishProfile."
    }

    $componentRoot = $temporaryComponentRoot
}
else {
    $componentRoot = (Resolve-Path -LiteralPath $ComponentPath -ErrorAction Stop).Path
}

if ($RequireSignature) {
    $signableFiles = @(Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.exe', '.dll', '.msi', '.msix') })

    if ($signableFiles.Count -eq 0) {
        throw 'No signable release files were found.'
    }

    foreach ($file in $signableFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate) {
            throw "A valid timestamped Authenticode signature is required: $($file.FullName)"
        }
    }
}

# Evidence from an earlier run must not become input to the new SBOM.
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}
if (Test-Path -LiteralPath $manifestRoot) {
    $artifactPrefix = $artifactRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedManifestRoot = (Resolve-Path -LiteralPath $manifestRoot -ErrorAction Stop).Path
    if (-not $resolvedManifestRoot.StartsWith(
        $artifactPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected manifest directory: $resolvedManifestRoot"
    }

    Remove-Item -LiteralPath $resolvedManifestRoot -Recurse -Force
}

# Hash release payloads first. The SBOM tool will then record both the payloads
# and this checksum file, while its own sidecar covers the generated manifest.
$checksumLines = @(
    Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object {
            $_.FullName -ne $checksumPath -and
            -not $_.FullName.StartsWith(
                $manifestRoot + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($artifactRoot.Length).
                TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash *$relativePath"
        }
)

[System.IO.File]::WriteAllLines(
    $checksumPath,
    $checksumLines,
    [System.Text.UTF8Encoding]::new($false)
)

$namespaceUniquePart = "gxTempMonitor/$PackageVersion/$artifactName"
$timestamp = $GenerationTimestamp.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

& $sbomCommand.Source generate `
    -b $artifactRoot `
    -bc $componentRoot `
    -pn 'gxTempMonitor' `
    -pv $PackageVersion `
    -ps 'gxmst' `
    -nsb 'https://github.com/gxmst/gxTempMonitor' `
    -nsu $namespaceUniquePart `
    -gt $timestamp `
    -D true
if ($LASTEXITCODE -ne 0) {
    throw 'SBOM generation failed.'
}

# Microsoft.Sbom.DotNetTool 4.1.5 emits a random SWID tag_id and enumerates
# packages/relationships concurrently. Normalize those fields so identical
# inputs also produce identical evidence.
$manifestPath = Join-Path $manifestRoot 'spdx_2.2\manifest.spdx.json'
if (Test-Path -LiteralPath $manifestPath) {
    $seedBytes = [System.Text.Encoding]::UTF8.GetBytes(
        "https://github.com/gxmst/gxTempMonitor/$namespaceUniquePart"
    )
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $seedHash = $hasher.ComputeHash($seedBytes)
    }
    finally {
        $hasher.Dispose()
    }

    $guidBytes = [byte[]]::new(16)
    [System.Array]::Copy($seedHash, $guidBytes, $guidBytes.Length)
    $stableTagId = [guid]::new($guidBytes).ToString('D')

    $manifestText = [System.IO.File]::ReadAllText($manifestPath)
    $tagIdPattern = '("referenceLocator"\s*:\s*"pkg:swid/[^\"]*[?&]tag_id=)[0-9a-fA-F-]{36}(\")'
    $tagIdMatches = [regex]::Matches($manifestText, $tagIdPattern)
    if ($tagIdMatches.Count -gt 0) {
        $evaluator = [System.Text.RegularExpressions.MatchEvaluator] {
            param($match)
            $match.Groups[1].Value + $stableTagId + $match.Groups[2].Value
        }
        $manifestText = [regex]::Replace($manifestText, $tagIdPattern, $evaluator)
    }

    $manifestObject = $manifestText | ConvertFrom-Json
    if ($null -ne $manifestObject.PSObject.Properties['packages']) {
        foreach ($package in $manifestObject.packages) {
            if ($null -ne $package.PSObject.Properties['externalRefs']) {
                $package.externalRefs = @($package.externalRefs | Sort-Object `
                    referenceCategory, referenceType, referenceLocator)
            }
            if ($null -ne $package.PSObject.Properties['checksums']) {
                $package.checksums = @($package.checksums | Sort-Object algorithm, checksumValue)
            }
            if ($null -ne $package.PSObject.Properties['hasFiles']) {
                $package.hasFiles = @($package.hasFiles | Sort-Object)
            }
        }
        $manifestObject.packages = @($manifestObject.packages | Sort-Object `
            @{ Expression = { if ($_.SPDXID -eq 'SPDXRef-RootPackage') { 0 } else { 1 } } }, `
            @{ Expression = { $_.SPDXID } })
    }
    if ($null -ne $manifestObject.PSObject.Properties['files']) {
        foreach ($file in $manifestObject.files) {
            if ($null -ne $file.PSObject.Properties['checksums']) {
                $file.checksums = @($file.checksums | Sort-Object algorithm, checksumValue)
            }
        }
        $manifestObject.files = @($manifestObject.files | Sort-Object SPDXID)
    }
    if ($null -ne $manifestObject.PSObject.Properties['relationships']) {
        $manifestObject.relationships = @($manifestObject.relationships | Sort-Object `
            spdxElementId, relationshipType, relatedSpdxElement)
    }
    if ($null -ne $manifestObject.PSObject.Properties['documentDescribes']) {
        $manifestObject.documentDescribes = @($manifestObject.documentDescribes | Sort-Object)
    }
    if ($null -ne $manifestObject.PSObject.Properties['creationInfo'] -and
        $null -ne $manifestObject.creationInfo.PSObject.Properties['creators']) {
        $manifestObject.creationInfo.creators = @($manifestObject.creationInfo.creators | Sort-Object)
    }

    $manifestText = $manifestObject | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $manifestText,
        [System.Text.UTF8Encoding]::new($false)
    )

    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$manifestPath.sha256",
        $manifestHash,
        [System.Text.UTF8Encoding]::new($false)
    )
}

$validationOutput = Join-Path (
    [System.IO.Path]::GetTempPath()
) "gxTempMonitor-sbom-validation-$PID-$([guid]::NewGuid().ToString('N')).json"

try {
    & $sbomCommand.Source validate -b $artifactRoot -o $validationOutput -mi 'SPDX:2.2'
    if ($LASTEXITCODE -ne 0) {
        throw 'SBOM validation failed.'
    }
}
finally {
    if (Test-Path -LiteralPath $validationOutput) {
        Remove-Item -LiteralPath $validationOutput -Force
    }
}

Write-Host "SBOM: $(Join-Path $artifactRoot '_manifest')"
Write-Host "Checksums: $checksumPath"
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($temporaryComponentRoot) -and
        (Test-Path -LiteralPath $temporaryComponentRoot)) {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).
            TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryComponentRoot)
        if (-not $resolvedTemporaryRoot.StartsWith(
            $tempRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected component directory: $resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
