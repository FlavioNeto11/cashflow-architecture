using System.Globalization;
using RabbitMQ.Client;

namespace CashFlow.Messaging;

/// <summary>
/// Conexão com o broker. Lida de chaves "RabbitMq:*" por uma função de busca, para servir tanto ao
/// IConfiguration dos hosts quanto às variáveis de ambiente do recon-tool (que não referencia ASP.NET).
/// </summary>
public sealed record RabbitMqOptions(string Host, int Port, string User, string Password, string VirtualHost)
{
    public const string HostKey = "RabbitMq:Host";
    public const string PortKey = "RabbitMq:Port";
    public const string UserKey = "RabbitMq:User";
    public const string PasswordKey = "RabbitMq:Password";
    public const string VirtualHostKey = "RabbitMq:VirtualHost";

    /// <summary>Falha cedo (e com o nome da chave, nunca o valor) quando a configuração obrigatória falta.</summary>
    public static RabbitMqOptions From(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var port = lookup(PortKey) is { Length: > 0 } text ? int.Parse(text, CultureInfo.InvariantCulture) : 5672;
        return new RabbitMqOptions(
            Required(lookup, HostKey), port, Required(lookup, UserKey), Required(lookup, PasswordKey),
            lookup(VirtualHostKey) is { Length: > 0 } vhost ? vhost : "/");
    }

    /// <summary>Busca em variáveis de ambiente com a convenção do .NET ("RabbitMq:Host" =&gt; "RabbitMq__Host").</summary>
    public static string? FromEnvironment(string key) =>
        Environment.GetEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal));

    /// <summary>
    /// Recuperação automática do cliente DESLIGADA: cada componente tem o próprio laço de reconexão com backoff e
    /// precisa saber quando o canal caiu (para pausar, devolver o lease ou parar), em vez de ter canais trocados por baixo.
    /// </summary>
    public ConnectionFactory CreateFactory(string clientName) => new()
    {
        HostName = Host,
        Port = Port,
        UserName = User,
        Password = Password,
        VirtualHost = VirtualHost,
        ClientProvidedName = clientName,
        AutomaticRecoveryEnabled = false,
        TopologyRecoveryEnabled = false,
        RequestedHeartbeat = TimeSpan.FromSeconds(30),
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Sem usuário nem senha: seguro para logs.</summary>
    public override string ToString() => $"amqp://{Host}:{Port.ToString(CultureInfo.InvariantCulture)}{VirtualHost}";

    private static string Required(Func<string, string?> lookup, string key) =>
        lookup(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Configuração obrigatória ausente: {key}.");
}
