using System.Security.Cryptography.X509Certificates;

namespace CashFlow.Hosting.Tls;

/// <summary>Carga de certificados PEM para Kestrel e para o HttpClient do gateway.</summary>
public static class CertificateFiles
{
    /// <summary>
    /// Certificado + chave (PEM). No Windows a chave de CreateFromPemFile é efêmera e o SChannel recusa usá-la num
    /// handshake ("Authentication failed because the platform does not support ephemeral keys" / "No credentials are
    /// available in the security package", visto em docs/testing/evidence/f5/dotnet-test-spike-smoke.log); o round-trip
    /// por PKCS#12 com chave NÃO efêmera resolve. Em Linux (containers) a chave efêmera funciona e evita tocar no disco.
    /// </summary>
    public static X509Certificate2 LoadWithKey(string certificatePath, string keyPath)
    {
        using var ephemeral = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        return WithUsableKey(ephemeral);
    }

    public static X509Certificate2 WithUsableKey(X509Certificate2 certificateWithEphemeralKey)
    {
        // Windows: DefaultKeySet grava a chave no perfil do usuário só enquanto o objeto vive (apagada no Dispose).
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12(certificateWithEphemeralKey.Export(X509ContentType.Pkcs12), password: null, flags);
    }

    /// <summary>Um ou mais certificados de CA em PEM (sem chave).</summary>
    public static X509Certificate2Collection LoadCertificates(string pemPath)
    {
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(pemPath);
        if (collection.Count == 0)
        {
            throw new InvalidOperationException($"Nenhum certificado em {Path.GetFileName(pemPath)}.");
        }

        return collection;
    }
}
