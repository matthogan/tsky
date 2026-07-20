$pfxPath = "C:\\code\\github\\tsky\\.temp\\gateway.pfx"
$pwd = Read-Host "PFX password" -AsSecureString
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, $pwd)
$pin = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($cert.RawData)).ToLowerInvariant()
$pin
