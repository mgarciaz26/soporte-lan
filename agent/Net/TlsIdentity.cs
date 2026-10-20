using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SoporteLan.Agent.Net;

/// <summary>
/// Certificado autofirmado del agente. La confianza no viene de una CA sino
/// del pinning: el visor compara la huella SHA-256 con la que se anuncia por
/// UDP antes de hablar TLS.
/// </summary>
internal static class TlsIdentity
{
    public static X509Certificate2 LoadOrCreate(out string fingerprintHex)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoporteLAN");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "agent.pfx");
        X509Certificate2 certificate;

        if (File.Exists(path))
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path, password: null, X509KeyStorageFlags.Exportable);
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=SoporteLAN Agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            using var selfSigned = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddYears(20));

            var pfx = selfSigned.Export(X509ContentType.Pfx);
            File.WriteAllBytes(path, pfx);

            certificate = X509CertificateLoader.LoadPkcs12(
                pfx, password: null, X509KeyStorageFlags.Exportable);
        }

        fingerprintHex = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return certificate;
    }
}
