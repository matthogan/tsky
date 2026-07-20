$pfxPath = "C:\\code\\github\\tsky\\.temp\\gateway.pfx"
$outPem = "C:\\code\\github\\tsky\\.temp\\gateway-ca.pem"
$pwd = Read-Host "PFX password" -AsSecureString
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $pwd)
$base64 = [Convert]::ToBase64String($cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert), 'InsertLineBreaks')
@("-----BEGIN CERTIFICATE-----",$base64,"-----END CERTIFICATE-----") | Set-Content -Path $outPem -Encoding ascii
