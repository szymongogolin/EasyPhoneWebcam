param(
    [string]$IpAddress,
    [switch]$NewCertificate
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runtime = Join-Path $root 'runtime\mediamtx'
$certDir = Join-Path $root 'runtime\cert'
$version = '1.21.1'

New-Item -ItemType Directory -Force -Path $runtime, $certDir | Out-Null

function Get-LanIPv4 {
    $activeInterfaces = Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.ConnectionState -eq 'Connected' } |
        Select-Object -ExpandProperty InterfaceIndex
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $activeInterfaces -contains $_.InterfaceIndex } |
        Sort-Object -Property InterfaceIndex |
        Select-Object -First 1 -ExpandProperty IPAddress
}

function Get-DerLengthBytes([int]$Length) {
    if ($Length -lt 128) { return ,([byte[]]@([byte]$Length)) }
    $lengthBytes = [System.Collections.Generic.List[byte]]::new()
    while ($Length -gt 0) {
        $lengthBytes.Insert(0, [byte]($Length -band 0xff))
        $Length = [int][Math]::Floor($Length / 256)
    }
    $result = [System.Collections.Generic.List[byte]]::new()
    $result.Add([byte](0x80 -bor $lengthBytes.Count))
    $result.AddRange($lengthBytes)
    return ,$result.ToArray()
}

function ConvertTo-DerInteger([byte[]]$Value) {
    $start = 0
    while ($start -lt ($Value.Length - 1) -and $Value[$start] -eq 0) { $start++ }
    $valueLength = $Value.Length - $start
    $needsLeadingZero = ($Value[$start] -band 0x80) -ne 0
    $contentLength = $valueLength + [int]$needsLeadingZero
    $result = [System.Collections.Generic.List[byte]]::new()
    $result.Add([byte]0x02)
    $result.AddRange((Get-DerLengthBytes $contentLength))
    if ($needsLeadingZero) { $result.Add([byte]0) }
    for ($i = $start; $i -lt $Value.Length; $i++) { $result.Add($Value[$i]) }
    return ,$result.ToArray()
}

function ConvertTo-Pem([string]$Label, [byte[]]$Data) {
    $base64 = [Convert]::ToBase64String($Data)
    $lines = for ($i = 0; $i -lt $base64.Length; $i += 64) { $base64.Substring($i, [Math]::Min(64, $base64.Length - $i)) }
    return "-----BEGIN $Label-----`n$($lines -join "`n")`n-----END $Label-----`n"
}

function ConvertRsaPrivateKeyToPkcs1([System.Security.Cryptography.RSAParameters]$Parameters) {
    $content = [System.Collections.Generic.List[byte]]::new()
    $content.AddRange((ConvertTo-DerInteger ([byte[]]@(0))))
    $content.AddRange((ConvertTo-DerInteger $Parameters.Modulus))
    $content.AddRange((ConvertTo-DerInteger $Parameters.Exponent))
    $content.AddRange((ConvertTo-DerInteger $Parameters.D))
    $content.AddRange((ConvertTo-DerInteger $Parameters.P))
    $content.AddRange((ConvertTo-DerInteger $Parameters.Q))
    $content.AddRange((ConvertTo-DerInteger $Parameters.DP))
    $content.AddRange((ConvertTo-DerInteger $Parameters.DQ))
    $content.AddRange((ConvertTo-DerInteger $Parameters.InverseQ))
    $sequence = [System.Collections.Generic.List[byte]]::new()
    $sequence.Add([byte]0x30)
    $sequence.AddRange((Get-DerLengthBytes $content.Count))
    $sequence.AddRange($content)
    return ,$sequence.ToArray()
}

$ip = if ($IpAddress) { $IpAddress } else { Get-LanIPv4 }
if (-not $ip) { throw 'Nie znaleziono aktywnego adresu IPv4. Polacz PC z ta sama siecia Wi-Fi co telefony.' }
try { $parsedIp = [System.Net.IPAddress]::Parse($ip) } catch { throw "Niepoprawny adres IP: $ip" }
if ($parsedIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or [System.Net.IPAddress]::IsLoopback($parsedIp)) { throw 'Podaj adres IPv4 dostepny dla telefonow w sieci lokalnej.' }

$certHostPath = Join-Path $certDir 'server-ip.txt'
$thumbprintPath = Join-Path $certDir 'server-thumbprint.txt'
$storedCertificateExists = (Test-Path $thumbprintPath) -and (Test-Path "Cert:\CurrentUser\My\$((Get-Content $thumbprintPath -Raw).Trim())")
$certificateNeedsCreation = $NewCertificate -or -not (Test-Path (Join-Path $certDir 'server.crt')) -or -not (Test-Path (Join-Path $certDir 'server.key')) -or -not (Test-Path (Join-Path $certDir 'PhoneCam-RootCA.cer')) -or -not $storedCertificateExists -or -not (Test-Path $certHostPath) -or ((Get-Content $certHostPath -Raw).Trim() -ne $ip)
if ($certificateNeedsCreation) {
    $oldThumbprintPath = Join-Path $certDir 'server-thumbprint.txt'
    if (Test-Path $oldThumbprintPath) {
        $oldThumbprint = (Get-Content $oldThumbprintPath -Raw).Trim()
        if ($oldThumbprint) { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$oldThumbprint" -Force -ErrorAction SilentlyContinue }
    }
    $caKey = [System.Security.Cryptography.RSA]::Create(3072)
    $caReq = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=PhoneCam Local Test CA', $caKey, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $caReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $caReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
    $caReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($caReq.PublicKey, $false))
    $ca = $caReq.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(5))

    $serverKey = [System.Security.Cryptography.RSA]::Create(2048)
    $serverReq = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$ip", $serverKey, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddIpAddress([System.Net.IPAddress]::Parse($ip))
    $san.AddDnsName('localhost')
    $serverReq.CertificateExtensions.Add($san.Build())
    $serverReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $serverReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
    $eku = [System.Security.Cryptography.OidCollection]::new()
    [void]$eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $serverReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
    $serial = [byte[]]::new(16)
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $random.GetBytes($serial)
    $random.Dispose()
    $serverPublic = $serverReq.Create($ca, [DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(2), $serial)
    $server = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($serverPublic, $serverKey)

    $serverCertificatePem = ConvertTo-Pem 'CERTIFICATE' $server.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    $serverPrivateKeyPem = ConvertTo-Pem 'RSA PRIVATE KEY' (ConvertRsaPrivateKeyToPkcs1 ($serverKey.ExportParameters($true)))
    Set-Content -NoNewline -Encoding ascii (Join-Path $certDir 'server.crt') -Value $serverCertificatePem
    Set-Content -NoNewline -Encoding ascii (Join-Path $certDir 'server.key') -Value $serverPrivateKeyPem
    [IO.File]::WriteAllBytes((Join-Path $certDir 'PhoneCam-RootCA.cer'), $ca.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $pfxPath = Join-Path $certDir 'server.pfx'
    [IO.File]::WriteAllBytes($pfxPath, $server.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'PhoneCam-local-only'))
    $importedServerCertificate = Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation 'Cert:\CurrentUser\My' -Password (ConvertTo-SecureString 'PhoneCam-local-only' -AsPlainText -Force) -Exportable
    Set-Content -LiteralPath $thumbprintPath -Value $importedServerCertificate.Thumbprint -Encoding ascii
    Remove-Item -LiteralPath $pfxPath -Force
    Set-Content -LiteralPath $certHostPath -Value $ip -Encoding ascii
    $serverKey.Dispose(); $caKey.Dispose(); $ca.Dispose(); $server.Dispose()
}

$archive = Join-Path $runtime "mediamtx_v${version}_windows_amd64.zip"
$exe = Join-Path $runtime 'mediamtx.exe'
if (-not (Test-Path $exe)) {
    $url = "https://github.com/bluenviron/mediamtx/releases/download/v$version/mediamtx_v${version}_windows_amd64.zip"
    Write-Host "Pobieram MediaMTX v$version z oficjalnego repozytorium..."
    Invoke-WebRequest -Uri $url -OutFile $archive
    $checksumUrl = "https://github.com/bluenviron/mediamtx/releases/download/v$version/checksums.sha256"
    $checksumFile = Join-Path $runtime 'checksums.sha256'
    Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumFile
    $assetName = Split-Path $archive -Leaf
    $checksumLine = Get-Content -LiteralPath $checksumFile | Where-Object { $_ -match [regex]::Escape($assetName) } | Select-Object -First 1
    if (-not $checksumLine) { throw "Nie znaleziono sumy SHA-256 dla $assetName." }
    $expectedHash = ($checksumLine -split '\s+')[0].Trim()
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { Remove-Item -LiteralPath $archive -Force; throw 'Suma SHA-256 archiwum MediaMTX jest niezgodna.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $runtime -Force
    Remove-Item -LiteralPath $archive -Force
}

$config = @"
logLevel: info
logDestinations: [stdout]
rtmp: false
hls: false
srt: false
moq: false
rtsp: true
rtspAddress: 127.0.0.1:8554
rtspTransports: [tcp]
webrtc: true
webrtcAddress: :8889
webrtcEncryption: true
webrtcServerKey: "$($certDir.Replace('\','/'))/server.key"
webrtcServerCert: "$($certDir.Replace('\','/'))/server.crt"
webrtcAllowOrigins: ['https://${ip}:8443']
webrtcLocalUDPAddress: :8189
webrtcLocalTCPAddress: ''
webrtcIPsFromInterfaces: false
webrtcIPsFromInterfacesList: []
webrtcIPsFromInterfacesExcludeList: []
webrtcAdditionalHosts: ['$ip']
webrtcICEServers2: []
paths:
  iphone:
    source: publisher
  android:
    source: publisher
"@
$configPath = Join-Path $runtime 'mediamtx.yml'
[IO.File]::WriteAllText($configPath, $config, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host 'PhoneCam gotowy do testu przez lokalne Wi-Fi.' -ForegroundColor Green
Write-Host "Strona / iPhone: https://${ip}:8443/iphone"
Write-Host "Strona / Android: https://${ip}:8443/android"
Write-Host "Podglad iPhone na PC: ffplay -fflags nobuffer -flags low_delay -rtsp_transport tcp rtsp://127.0.0.1:8554/iphone"
Write-Host "Podglad Android na PC: ffplay -fflags nobuffer -flags low_delay -rtsp_transport tcp rtsp://127.0.0.1:8554/android"
Write-Host ''
Write-Host 'Strona wysyla obraz H.264 i nie prosi o mikrofon. Domyslnie iPhone ma 720p60, Xiaomi 720p30.'
Write-Host 'Przy pierwszym uruchomieniu zezwol aplikacji PhoneCam na ruch przychodzacy w Zaporze Windows dla sieci prywatnej.'
Write-Host 'Certyfikat pozostaje ten sam przy kolejnym uruchomieniu. Uzyj -NewCertificate, aby wygenerowac go ponownie.'
Write-Host ''
Write-Host "Certyfikat CA do zaufania na telefonach: $(Join-Path $certDir 'PhoneCam-RootCA.cer')"
Write-Host "Certyfikat CA pobierzesz z telefonu przez: http://${ip}:8787/PhoneCam-RootCA.cer"
Write-Host 'iPhone: zainstaluj pobrany profil, a potem w Ustawienia > Ogolne > To urzadzenie > Ustawienia zaufania certyfikatow wlacz pelne zaufanie dla PhoneCam Local Test CA.'
Write-Host 'Android: zainstaluj pobrany certyfikat jako CA/VPN w ustawieniach zabezpieczen. Nastepnie otworz adres telefonu ponownie.'
Write-Host 'Adresy dzialaja tylko wtedy, gdy telefon i PC sa w tej samej sieci lokalnej.'
Write-Host ''
$certShareProject = Join-Path $PSScriptRoot 'PhoneCam.CertShare.csproj'
$certShareOutput = Join-Path $runtime 'certshare'
dotnet build $certShareProject --configuration Release --output $certShareOutput --nologo
if ($LASTEXITCODE -ne 0) { throw 'Nie udalo sie zbudowac lokalnego serwera pobierania certyfikatu.' }
$certShareDll = Join-Path $certShareOutput 'PhoneCam.CertShare.dll'
$phonePage = Join-Path $root 'src\PhoneCam.Desktop\Web\phonecam.html'
$thumbprint = (Get-Content $thumbprintPath -Raw).Trim()
$certShareStdout = Join-Path $runtime 'certshare.log'
$certShareStderr = Join-Path $runtime 'certshare-error.log'
$certShareProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($certShareDll, (Join-Path $certDir 'PhoneCam-RootCA.cer'), $thumbprint, $phonePage) -PassThru -WindowStyle Hidden -RedirectStandardOutput $certShareStdout -RedirectStandardError $certShareStderr
try {
    Start-Sleep -Seconds 2
    if ($certShareProcess.HasExited) {
        Get-Content $certShareStderr -ErrorAction SilentlyContinue | Write-Host
        throw "Nie udalo sie uruchomic serwera strony. Log: $certShareStderr"
    }
    Write-Host "Certyfikat CA mozna pobrac telefonem z http://${ip}:8787/PhoneCam-RootCA.cer"
    & $exe $configPath
}
finally {
    if (-not $certShareProcess.HasExited) { Stop-Process -Id $certShareProcess.Id -Force }
}
