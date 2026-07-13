param(
    [string]$CertificatePath = (Join-Path $PSScriptRoot '../AppHost/configs/nats/certs/ws-cert.pem'),
    [string]$KeyPath = (Join-Path $PSScriptRoot '../AppHost/configs/nats/certs/ws-key.pem')
)

$certificateDirectory = Split-Path -Parent $CertificatePath

if ((Test-Path -LiteralPath $CertificatePath) -and (Test-Path -LiteralPath $KeyPath)) {
    return
}

New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=localhost',
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
    )

    $subjectAltNames = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $subjectAltNames.AddDnsName('localhost')
    $subjectAltNames.AddDnsName('nats')
    $subjectAltNames.AddIpAddress([System.Net.IPAddress]::Loopback)

    $request.CertificateExtensions.Add($subjectAltNames.Build())
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $false))
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment,
        $false
    ))

    $serverAuthOid = [System.Security.Cryptography.OidCollection]::new()
    [void]$serverAuthOid.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($serverAuthOid, $false))

    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $notAfter = $notBefore.AddYears(1)
    $certificate = $request.CreateSelfSigned($notBefore, $notAfter)
    try {
        [System.IO.File]::WriteAllText($CertificatePath, $certificate.ExportCertificatePem())
        [System.IO.File]::WriteAllText($KeyPath, $rsa.ExportPkcs8PrivateKeyPem())

        if ([System.OperatingSystem]::IsLinux() -or [System.OperatingSystem]::IsMacOS()) {
            [System.IO.File]::SetUnixFileMode(
                $CertificatePath,
                [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::GroupRead -bor [System.IO.UnixFileMode]::OtherRead
            )
            [System.IO.File]::SetUnixFileMode(
                $KeyPath,
                [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
            )
        }
    }
    finally {
        $certificate.Dispose()
    }
}
finally {
    $rsa.Dispose()
}
