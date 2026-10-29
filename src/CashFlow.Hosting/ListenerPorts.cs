namespace CashFlow.Hosting;

/// <summary>
/// Portas dos dois listeners do Kestrel. A porta de saúde serve somente /health/live e /health/ready
/// (HTTP puro, rede interna); a porta de negócio serve a API e nunca /health/*. Em F5 a porta de
/// negócio passa a exigir mTLS; a de saúde continua sem TLS para os healthchecks do orquestrador.
/// </summary>
/// <param name="Business">Porta de negócio; 0 quando o host não tem API (worker).</param>
/// <param name="Health">Porta do listener de saúde.</param>
public sealed record ListenerPorts(int Business, int Health)
{
    public const int DefaultBusiness = 8080;
    public const int DefaultHealth = 8081;
}
