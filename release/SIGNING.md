# Authenticode signing and release evidence

Official Windows binaries should be Authenticode-signed with a certificate whose identity matches the publisher, and timestamped with an RFC 3161 timestamp service. Signing improves publisher identity and tamper detection; it does not guarantee that Microsoft SmartScreen, antivirus products, or game anti-cheat products will trust a new release immediately.

## Key handling

- Prefer a hardware-backed code-signing certificate, Azure Trusted Signing, or another managed signing service.
- Never commit a `.pfx`, private key, certificate password, cloud credential, or timestamp token to this repository.
- Grant the build identity access only to the signing operation. Do not export the private key into a normal CI artifact.
- Treat GitHub Actions artifacts produced by `release-candidates.yml` as **unsigned candidates**, not public releases.

## Release order

1. Publish into a new, empty output directory using one of the checked-in publish profiles.
2. Scan and smoke-test the candidate.
3. Sign every public `.exe`, `.dll`, `.msi`, or `.msix` and request an RFC 3161 timestamp.
4. Verify the signature and timestamp.
5. Generate `SHA256SUMS.txt` for the signed release payloads.
6. Generate and validate the SBOM; it records the payloads and checksum file, while its sidecar records the manifest hash.
7. Archive and upload without modifying the files again.

Signing changes a binary's hash. Any checksum or SBOM generated before signing must be discarded and regenerated.

## Local certificate-store example

Install the Windows SDK so that `signtool.exe` is available. Import the code-signing certificate into `CurrentUser\My` without exporting it into the repository, then run:

```powershell
$exe = 'publish/self-contained/win-x64/gxTempMonitor.exe'
./release/Sign-Artifacts.ps1 -CertificateThumbprint '<certificate thumbprint>' -Path $exe

Get-AuthenticodeSignature $exe | Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
```

For a machine-store certificate, add `-CertificateStore LocalMachine`. The helper deliberately accepts only a certificate-store thumbprint; it never accepts a PFX password on the command line.

## SBOM and checksums

The evidence helper uses Microsoft's SBOM tool and emits SPDX plus SHA-256 checksums:

```powershell
dotnet tool install --global Microsoft.Sbom.DotNetTool --version 4.1.5

./release/New-ReleaseEvidence.ps1 `
  -ArtifactDirectory 'publish/self-contained/win-x64' `
  -PackageVersion '6.0.0' `
  -GenerationTimestamp (git show -s --format=%cI).Trim() `
  -RequireSignature
```

The helper pins SBOM Tool 4.1.5 in CI, isolates component detection per publish profile, normalizes nondeterministic identifiers and array ordering, refreshes the manifest sidecar hash, and validates the final SPDX document. A fixed `-GenerationTimestamp` is required so repeated generation from the same release inputs stays reproducible. `SHA256SUMS.txt` covers release payloads; the SBOM tool's adjacent `.sha256` file covers the manifest itself.

Verify downloaded files with:

```powershell
Get-FileHash ./gxTempMonitor.exe -Algorithm SHA256
Get-AuthenticodeSignature ./gxTempMonitor.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
```

The repository does not contain a signing certificate and the build does not pretend to be signed. Configure signing only in a protected release environment.
