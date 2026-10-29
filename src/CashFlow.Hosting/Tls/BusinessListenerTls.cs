using System.Security.Authentication;
using CashFlow.Hosting.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashFlow.Hosting.Tls;

/// <summary>
/// Liga TLS no listener de negócio do Kestrel (security.md 6): TLS 1.2+; com RequireClientCertificate (APIs) o
/// certificado de cliente é obrigatório e validado por <see cref="ClientCertificateValidator"/>; sem ele (borda do
/// gateway) é TLS de servidor apenas.
/// </summary>
internal static class BusinessListenerTls
{
    public static void Configure(ListenOptions listener, TlsSettings settings, IServiceProvider services)
    {
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var serverCertificate = CertificateFiles.LoadWithKey(settings.ServerCertificate!, settings.ServerKey!);
        var validator = settings.RequireClientCertificate
            ? ClientCertificateValidator.FromSettings(settings, services.GetService<SecurityMetrics>(), loggerFactory.CreateLogger<ClientCertificateValidator>())
            : null;

        listener.UseHttps(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            https.HandshakeTimeout = TimeSpan.FromSeconds(10);
            https.CheckCertificateRevocation = false; // CA local sem CRL/OCSP; revogação = blocklist de thumbprint (GAP parcial)
            if (validator is not null)
            {
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = validator.Validate;
            }
            else
            {
                https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
            }
        });

        loggerFactory.CreateLogger("CashFlow.Hosting.Tls").LogInformation(
            "Listener de negócio com TLS: certificado de servidor {Subject} (expira {NotAfter:u}); certificado de cliente {Mode}.",
            serverCertificate.Subject,
            serverCertificate.NotAfter.ToUniversalTime(),
            validator is null ? "não exigido (borda)" : $"obrigatório, nomes aceitos: {settings.AllowedClientNames}");
    }
}
