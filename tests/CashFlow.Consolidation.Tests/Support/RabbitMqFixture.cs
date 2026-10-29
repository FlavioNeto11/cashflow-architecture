using System.Net;
using System.Net.Sockets;
using CashFlow.Messaging;
using CashFlow.Persistence.Tests;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace CashFlow.Consolidation.Tests.Support;

/// <summary>
/// rabbitmq:4-management real com os MESMOS arquivos de infra/rabbitmq usados pelo Compose (entrypoint, rabbitmq.conf,
/// definitions.json). A porta AMQP do host é fixa (escolhida livre no início) para que parar e religar o broker
/// (T-05) mantenha o endereço; o Testcontainers sortearia outra porta a cada start.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    // Credenciais descartáveis do container de teste. O usuário da APLICAÇÃO recebe do entrypoint (F6) só as permissões
    // mínimas (configure vazio; write/read restritos à topologia); o de OPERAÇÃO (RABBITMQ_OPS_*) é administrador e serve
    // apenas às manipulações de topologia dos testes (desfazer/refazer binding, declaração passiva de recurso inexistente).
    public const string User = "test_rabbit_user";
    public const string Password = "test_rabbit_pw";
    public const string OpsUser = "test_rabbit_ops";
    public const string OpsPassword = "test_rabbit_ops_pw";

    private readonly RabbitMqContainer _container;
    private readonly int _hostPort = FreeTcpPort();

    public RabbitMqFixture()
    {
        var infra = Path.Combine(RepositoryPaths.Root, "infra", "rabbitmq");
        _container = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(User)
            .WithPassword(Password)
            .WithEnvironment("RABBITMQ_OPS_USER", OpsUser)
            .WithEnvironment("RABBITMQ_OPS_PASS", OpsPassword)
            .WithResourceMapping(new FileInfo(Path.Combine(infra, "entrypoint.sh")), "/opt/cashflow/")
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(infra, "rabbitmq.conf")), "/etc/rabbitmq/conf.d/20-cashflow.conf")
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(infra, "definitions.json")), "/etc/rabbitmq/definitions.d/10-topology.json")
            .WithEntrypoint("bash", "/opt/cashflow/entrypoint.sh")
            .WithPortBinding(_hostPort, 5672)
            // Testcontainers publica em 0.0.0.0 por padrão (banco/broker de teste com senha fixa alcançável pela rede local);
            // restringe todas as publicações ao loopback (GAP-F5-10; mesma técnica do KeycloakFixture).
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var binding in (parameters.HostConfig?.PortBindings?.Values ?? []).SelectMany(b => b))
                {
                    binding.HostIP = "127.0.0.1";
                }
            })
            .Build();
    }

    public RabbitMqOptions Options => new(_container.Hostname, _hostPort, User, Password, "/");

    /// <summary>Usuário de operação (administrator): só para o próprio teste alterar a topologia, nunca para o código sob teste.</summary>
    public RabbitMqOptions OpsOptions => new(_container.Hostname, _hostPort, OpsUser, OpsPassword, "/");

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task StopAsync() => _container.StopAsync();

    public Task StartAsync() => _container.StartAsync();

    public async Task<string> RabbitmqctlAsync(params string[] arguments)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "-q", .. arguments]);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return result.Stdout;
    }

    public async Task<IConnection> ConnectAsync(string clientName = "tests") =>
        await Options.CreateFactory(clientName).CreateConnectionAsync();

    public async Task<IConnection> ConnectAsOperatorAsync(string clientName = "tests-ops") =>
        await OpsOptions.CreateFactory(clientName).CreateConnectionAsync();

    /// <summary>
    /// Consumidores registrados (rabbitmqctl list_consumers). Observado com RabbitMQ 4.4, fila quorum: um consumidor cancelado com entregas
    /// ainda sem ACK continua contado em queue.declare-ok/list_queues até elas serem liquidadas; list_consumers não o lista.
    /// </summary>
    public async Task<int> RegisteredConsumersAsync(string queue)
    {
        var output = await RabbitmqctlAsync("list_consumers", "queue_name");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => line == queue);
    }

    /// <summary>Contagens via declaração passiva (mensagens prontas e consumidores; veja RegisteredConsumersAsync).</summary>
    public async Task<(uint Messages, uint Consumers)> QueueStateAsync(string queue)
    {
        await using var connection = await ConnectAsync("tests-inspect");
        await using var channel = await connection.CreateChannelAsync();
        var ok = await channel.QueueDeclarePassiveAsync(queue);
        return (ok.MessageCount, ok.ConsumerCount);
    }

    /// <summary>pids das conexões cujo connection_name (ClientProvidedName) começa com o prefixo.</summary>
    public async Task<List<string>> ConnectionPidsAsync(string clientNamePrefix)
    {
        var output = await RabbitmqctlAsync("list_connections", "pid", "client_properties");
        var marker = "{\"connection_name\",\"" + clientNamePrefix;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains(marker, StringComparison.Ordinal))
            .Select(line => line.Split('\t')[0])
            .ToList();
    }

    /// <summary>Derruba no broker (close_connection) as conexões do cliente: queda de conexão sem parar o broker.</summary>
    public async Task<List<string>> CloseConnectionsAsync(string clientNamePrefix)
    {
        var pids = await ConnectionPidsAsync(clientNamePrefix);
        foreach (var pid in pids)
        {
            await RabbitmqctlAsync("close_connection", pid, "teste: queda forçada da conexão");
        }

        return pids;
    }

    /// <summary>Esvazia uma fila entre testes (os testes da coleção compartilham o broker).</summary>
    public async Task PurgeAsync(params string[] queues)
    {
        await using var connection = await ConnectAsync("tests-purge");
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in queues)
        {
            await channel.QueuePurgeAsync(queue);
        }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
