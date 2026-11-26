# Makes the self-signed TLS certificate of the local play stack (docs/LOCAL_PLAY.md). Works in Windows PowerShell 5.1 and PowerShell 7. It touches no Windows
# certificate store: it only writes files into -Folder (gitignored, never committed).
#   server.pfx   certificate and private key, protected by -Password; the server container reads it
#   server.pem   the certificate alone (public); the game build is told to trust exactly this one
# The certificate is for 127.0.0.1 and localhost only, may not sign other certificates, and lasts one year.
param(
    [Parameter(Mandatory = $true)] [string] $Folder,
    [Parameter(Mandatory = $true)] [string] $Password
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$Folder = (Resolve-Path $Folder).Path

$names = "System.Security.Cryptography.X509Certificates"
$rsa = [System.Security.Cryptography.RSA]::Create()
if ($rsa.KeySize -lt 2048 -or $PSVersionTable.PSVersion.Major -lt 6) {
    # Windows PowerShell's RSA.Create() is the old 1024-bit provider.
    $rsa.Dispose()
    $rsa = New-Object System.Security.Cryptography.RSACng 2048
}
$certificate = $null
try {
    $request = New-Object "$names.CertificateRequest" -ArgumentList @(
        "CN=MMORPG local play", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $alternative = New-Object "$names.SubjectAlternativeNameBuilder"
    $alternative.AddDnsName("localhost")
    $alternative.AddIpAddress([System.Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($alternative.Build())
    $request.CertificateExtensions.Add((New-Object "$names.X509BasicConstraintsExtension" -ArgumentList @($false, $false, 0, $true)))
    $usage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]"DigitalSignature, KeyEncipherment"
    $request.CertificateExtensions.Add((New-Object "$names.X509KeyUsageExtension" -ArgumentList @($usage, $true)))
    $purposes = New-Object System.Security.Cryptography.OidCollection
    [void]$purposes.Add((New-Object System.Security.Cryptography.Oid "1.3.6.1.5.5.7.3.1"))   # server authentication
    $request.CertificateExtensions.Add((New-Object "$names.X509EnhancedKeyUsageExtension" -ArgumentList @($purposes, $false)))

    $now = [DateTimeOffset]::UtcNow
    $certificate = $request.CreateSelfSigned($now.AddDays(-1), $now.AddDays(365))

    [IO.File]::WriteAllBytes((Join-Path $Folder "server.pfx"), $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
    $body = [Convert]::ToBase64String($certificate.RawData, [Base64FormattingOptions]::InsertLineBreaks) -replace "`r", ""
    [IO.File]::WriteAllText((Join-Path $Folder "server.pem"), "-----BEGIN CERTIFICATE-----`n$body`n-----END CERTIFICATE-----`n")
}
finally {
    if ($certificate) { $certificate.Dispose() }
    $rsa.Dispose()
}
