using System.Formats.Asn1;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PhoneCam.Desktop.Hosting;

public sealed record ServerCertificate(X509Certificate2 Certificate, string CertificatePemPath, string PrivateKeyPemPath);

/// <summary>
/// Local PKI. The root CA is created once and its private key stays in the Windows per-user key store
/// (non-exportable), so the phone trusts it once. Only the short-lived server certificate is reissued
/// when the PC's IP address changes. Name constraints limit the CA to private IPv4 ranges, so even a
/// misused CA key cannot impersonate public websites on the phone.
/// </summary>
internal static class LocalCertificates
{
    /// <summary>Name of the persisted CNG key; overridable so checks never touch the real CA.</summary>
    internal static string CaKeyName { get; set; } = "PhoneCam Local CA";
    private const string CaSubject = "CN=PhoneCam Local CA, O=PhoneCam";

    public static string RootCaPath => Path.Combine(AppPaths.Certificates, "PhoneCam-RootCA.cer");
    private static string ServerCertificatePath => Path.Combine(AppPaths.Certificates, "server.crt");
    private static string ServerKeyPath => Path.Combine(AppPaths.Certificates, "server.key");

    public static ServerCertificate EnsureServerCertificate(IPAddress address)
    {
        X509Certificate2 ca = EnsureRootCa();
        if (TryLoadServerCertificate(address, ca) is { } existing)
            return new ServerCertificate(existing, ServerCertificatePath, ServerKeyPath);

        using RSA key = RSA.Create(2048);
        // No CN: the IP lives only in the SAN, so no validator can mistake the subject for a DNS name.
        var request = new CertificateRequest("O=PhoneCam, OU=Local server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build(critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        // Apple rejects TLS server certificates valid for more than 825 days; stay well under it.
        using X509Certificate2 issued = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(397), serial);
        File.WriteAllText(ServerCertificatePath, issued.ExportCertificatePem());
        File.WriteAllText(ServerKeyPath, key.ExportRSAPrivateKeyPem());

        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
        return new ServerCertificate(ForSchannel(withKey), ServerCertificatePath, ServerKeyPath);
    }

    private static X509Certificate2? TryLoadServerCertificate(IPAddress address, X509Certificate2 ca)
    {
        if (!File.Exists(ServerCertificatePath) || !File.Exists(ServerKeyPath))
            return null;
        try
        {
            using X509Certificate2 loaded = X509Certificate2.CreateFromPemFile(ServerCertificatePath, ServerKeyPath);
            bool matchesAddress = loaded.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                .Any(e => e.EnumerateIPAddresses().Contains(address));
            ReadOnlyMemory<byte>? issuerKeyId = loaded.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
            ReadOnlyMemory<byte>? caKeyId = ca.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes;
            bool issuedByCurrentCa = issuerKeyId is { } a && caKeyId is { } b && a.Span.SequenceEqual(b.Span);
            bool fresh = loaded.NotAfter > DateTime.Now.AddDays(30);
            return matchesAddress && issuedByCurrentCa && fresh ? ForSchannel(loaded) : null;
        }
        catch (CryptographicException) { return null; }
    }

    private static X509Certificate2 EnsureRootCa()
    {
        if (CngKey.Exists(CaKeyName) && File.Exists(RootCaPath))
        {
            X509Certificate2 stored = X509CertificateLoader.LoadCertificateFromFile(RootCaPath);
            if (stored.NotAfter > DateTime.Now.AddDays(30))
                return stored.CopyWithPrivateKey(new RSACng(CngKey.Open(CaKeyName)));
        }

        // Missing or expired: start over. The phone will need to trust the new CA once.
        if (CngKey.Exists(CaKeyName))
            CngKey.Open(CaKeyName).Delete();
        var parameters = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        var rsa = new RSACng(CngKey.Create(CngAlgorithm.Rsa, CaKeyName, parameters));

        var request = new CertificateRequest(CaSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(BuildNameConstraints());
        X509Certificate2 ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(RootCaPath, ca.Export(X509ContentType.Cert));
        File.Delete(ServerCertificatePath);
        File.Delete(ServerKeyPath);
        return ca;
    }

    /// <summary>RFC 5280 NameConstraints: only private IPv4 ranges, and DNS names only under a reserved .invalid name.</summary>
    private static X509Extension BuildNameConstraints()
    {
        byte[][] privateRanges =
        [
            [10, 0, 0, 0, 255, 0, 0, 0],
            [172, 16, 0, 0, 255, 240, 0, 0],
            [192, 168, 0, 0, 255, 255, 0, 0],
        ];
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            foreach (byte[] range in privateRanges)
                using (writer.PushSequence())
                    writer.WriteOctetString(range, new Asn1Tag(TagClass.ContextSpecific, 7));
            using (writer.PushSequence())
                writer.WriteCharacterString(UniversalTagNumber.IA5String, "phonecam.invalid", new Asn1Tag(TagClass.ContextSpecific, 2));
        }
        return new X509Extension("2.5.29.30", writer.Encode(), critical: true);
    }

    /// <summary>SChannel (Kestrel on Windows) cannot use ephemeral PEM keys; round-trip through PKCS#12.</summary>
    private static X509Certificate2 ForSchannel(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
}
