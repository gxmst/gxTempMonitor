[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $CertificateThumbprint,

    [Parameter(Mandatory, ValueFromPipeline)]
    [ValidateNotNullOrEmpty()]
    [string[]] $Path,

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string] $CertificateStore = 'CurrentUser',

    [string] $SignToolPath,

    [ValidateNotNullOrEmpty()]
    [uri] $TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-MicrosoftSignTool {
    param([Parameter(Mandatory)][string] $CandidatePath)

    $signature = Get-AuthenticodeSignature -LiteralPath $CandidatePath
    return $signature.Status -eq 'Valid' -and
        $null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.Subject -match '(^|,\s*)O=Microsoft Corporation(,|$)'
}

function Find-SignTool {
    param([string] $ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = Resolve-Path -LiteralPath $ExplicitPath -ErrorAction Stop
        $item = Get-Item -LiteralPath $resolved.Path -ErrorAction Stop
        if ($item.PSIsContainer -or $item.Name -ne 'signtool.exe') {
            throw "Expected a signtool.exe file: $ExplicitPath"
        }
        if (-not (Test-MicrosoftSignTool -CandidatePath $item.FullName)) {
            throw "The selected signtool.exe does not have a valid Microsoft signature: $($item.FullName)"
        }

        return $item.FullName
    }

    if ([string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        throw 'ProgramFiles(x86) is unavailable; pass -SignToolPath explicitly.'
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidates = Get-ChildItem -LiteralPath $kitsRoot -Filter 'signtool.exe' -File -Recurse |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } |
            Sort-Object FullName -Descending

        foreach ($candidate in $candidates) {
            if (Test-MicrosoftSignTool -CandidatePath $candidate.FullName) {
                return $candidate.FullName
            }
        }
    }

    throw 'A Microsoft-signed signtool.exe was not found under Windows Kits. Install the Windows 10/11 SDK or pass -SignToolPath.'
}

$thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
$certificatePath = "Cert:\$CertificateStore\My\$thumbprint"
$certificate = Get-Item -LiteralPath $certificatePath -ErrorAction Stop

if (-not $certificate.HasPrivateKey) {
    throw "The certificate $thumbprint has no accessible private key."
}

$codeSigningEku = @(
    $certificate.EnhancedKeyUsageList |
        Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' }
)
if ($codeSigningEku.Count -eq 0) {
    throw "The certificate $thumbprint is not valid for code signing."
}

$files = @(
    foreach ($item in $Path) {
        $resolved = Resolve-Path -LiteralPath $item -ErrorAction Stop
        if ((Get-Item -LiteralPath $resolved.Path).PSIsContainer) {
            throw "Expected a file but received a directory: $item"
        }

        $extension = [System.IO.Path]::GetExtension($resolved.Path).ToLowerInvariant()
        if ($extension -notin @('.exe', '.dll', '.msi', '.msix')) {
            throw "Unsupported Authenticode file type: $($resolved.Path)"
        }

        $resolved.Path
    }
)

$signTool = Find-SignTool -ExplicitPath $SignToolPath
$storeArgs = @('/s', 'My')
if ($CertificateStore -eq 'LocalMachine') {
    $storeArgs += '/sm'
}

foreach ($file in $files) {
    if (-not $PSCmdlet.ShouldProcess($file, 'Apply Authenticode signature')) {
        continue
    }

    & $signTool sign @storeArgs /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 /v $file
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed to sign: $file"
    }

    & $signTool verify /pa /all /v $file
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed: $file"
    }
}
