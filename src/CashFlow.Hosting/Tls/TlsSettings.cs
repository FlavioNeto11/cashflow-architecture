using Microsoft.Extensions.Configuration;

namespace CashFlow.Hosting.Tls;

/// <summary>
/// mTLS do listener de negócio (security.md 6; ADR-0011). Gate por configuração: sem Hosting:Tls:ServerCertificate o
/// listener fica em HTTP puro (testes e desenvolvimento); com ele, Kestrel apresenta o certificado de servidor e EXIGE
/// certificado de cliente emitido pela CA local, com EKU clientAuth e SAN em AllowedClientNames. Arquivos PEM, nunca
/// versionados (infra/pki/make-certs.sh gera em infra/pki/out/). O listener de saúde nunca usa TLS.
/// </summary>
public sealed class TlsSettings
{
    public const string Section = "Hosting:Tls";

    /// <summary>Certificado de servidor (PEM). Ausente = listener de negócio sem TLS.</summary>
    public string? ServerCertificate { get; set; }

    /// <summary>Chave privada do servidor (PEM, PKCS#8 ou tradicional).</summary>
    public string? ServerKey { get; set; }

    /// <summary>
    /// true (padrão, APIs): mTLS, certificado de cliente obrigatório. false (gateway na borda): TLS de servidor apenas;
    /// quem autentica o chamador é o JWT. Nunca use false numa API interna.
    /// </summary>
    public bool RequireClientCertificate { get; set; } = true;

    /// <summary>CA(s) confiável(is) para o certificado de cliente (PEM; pode conter mais de um certificado).</summary>
    public string? ClientCa { get; set; }

    /// <summary>Nomes (SAN DNS) aceitos no certificado de cliente; separados por ';', ',' ou espaço.</summary>
    public string AllowedClientNames { get; set; } = "gateway.internal";

    /// <summary>Arquivo opcional com thumbprints SHA-1 (hex, um por linha; '#' comenta) recusados; relido quando muda.</summary>
    public string? BlockedThumbprintsFile { get; set; }

    /// <summary>
    /// Opt-in explícito para o listener de negócio em HTTP puro fora de Development (C-F5-09). Sem ele, a partida falha
    /// quando ServerCertificate está ausente: TLS omitido por engano não pode subir em silêncio.
    /// </summary>
    public bool AllowPlainHttp { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(ServerCertificate);

    public IReadOnlyList<string> AllowedClientNameList =>
        AllowedClientNames.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static TlsSettings Load(IConfiguration configuration)
    {
        var settings = configuration.GetSection(Section).Get<TlsSettings>() ?? new TlsSettings();
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        RequireFile(ServerCertificate, nameof(ServerCertificate));
        RequireFile(ServerKey, nameof(ServerKey));
        if (!RequireClientCertificate)
        {
            return;
        }

        // Fail-closed: mTLS sem CA de cliente seria TLS unilateral chamado de mTLS.
        RequireFile(ClientCa, nameof(ClientCa));
        if (AllowedClientNameList.Count == 0)
        {
            throw new InvalidOperationException($"{Section}:AllowedClientNames deve listar ao menos um nome (SAN) de cliente aceito.");
        }

        if (string.IsNullOrWhiteSpace(BlockedThumbprintsFile))
        {
            BlockedThumbprintsFile = null; // variável de ambiente vazia = sem blocklist
        }
        else
        {
            RequireFile(BlockedThumbprintsFile, nameof(BlockedThumbprintsFile));
        }
    }

    private static void RequireFile(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{Section}:{name} é obrigatório quando {Section}:ServerCertificate está definido.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{Section}:{name}: arquivo não encontrado ({Path.GetFileName(path)}).");
        }
    }
}
