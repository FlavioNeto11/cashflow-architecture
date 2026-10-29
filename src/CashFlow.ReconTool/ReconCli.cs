using System.Globalization;
using CashFlow.Messaging;
using Npgsql;

namespace CashFlow.ReconTool;

/// <summary>
/// Linha de comando do recon-tool. Códigos de saída: 0 concluído; 1 falha (infraestrutura, inesperada ou publicação
/// não confirmada); 2 concluído com linhas abortadas, mensagens recusadas ou alvo não encontrado (repetir/investigar);
/// 64 uso incorreto.
/// </summary>
public static class ReconCli
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Attention = 2;
    public const int UsageError = 64;

    public const string SourceKey = "ConnectionStrings:Source";
    public const string DefaultKey = "ConnectionStrings:Default";

    /// <summary>Teto do intervalo por execução: limita a varredura de datas (feita fora do lock) e o tempo da execução.</summary>
    public const int MaxRangeDays = 366;

    private const string Usage = """
        uso:
          recon-tool reconcile --merchant <uuid> --from <yyyy-MM-dd> --to <yyyy-MM-dd>
          recon-tool rebuild   --merchant <uuid> --from <yyyy-MM-dd> --to <yyyy-MM-dd>
          recon-tool dlq redrive (--all | --id <message-id>)
          recon-tool dlq purge   (--all | --id <message-id>) --reason <texto>
        """;

    public static async Task<int> RunAsync(
        string[] args, Func<string, string?> config, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            switch (args)
            {
                case ["reconcile", .. var rest] when TryRange(rest, out var merchant, out var from, out var to, out var error):
                    return await ReconcileAsync(config, stdout, merchant, from, to, rebuild: false, cancellationToken);
                case ["rebuild", .. var rest] when TryRange(rest, out var merchant, out var from, out var to, out var error):
                    return await ReconcileAsync(config, stdout, merchant, from, to, rebuild: true, cancellationToken);
                case ["dlq", "redrive", "--all"]:
                    return await RedriveAsync(config, stdout, stderr, null, cancellationToken);
                case ["dlq", "redrive", "--id", { Length: > 0 } id]:
                    return await RedriveAsync(config, stdout, stderr, id, cancellationToken);
                case ["dlq", "purge", "--all", "--reason", { } purgeReason] when ValidReason(purgeReason):
                    return await PurgeAsync(config, stdout, stderr, null, purgeReason.Trim(), cancellationToken);
                case ["dlq", "purge", "--id", { Length: > 0 } id, "--reason", { } purgeReason] when ValidReason(purgeReason):
                    return await PurgeAsync(config, stdout, stderr, id, purgeReason.Trim(), cancellationToken);
                case ["reconcile" or "rebuild", .. var rest]:
                    TryRange(rest, out _, out _, out _, out var reason);
                    await stderr.WriteLineAsync(reason);
                    await stderr.WriteLineAsync(Usage);
                    return UsageError;
                default:
                    await stderr.WriteLineAsync(Usage);
                    return UsageError;
            }
        }
        catch (Exception error) when (error is NpgsqlException or RabbitMQ.Client.Exceptions.RabbitMQClientException
            or InvalidOperationException or IOException or TimeoutException)
        {
            // Tipo e mensagem apenas: nem cadeia de conexão nem payload.
            await stderr.WriteLineAsync($"falha: {error.GetType().Name}: {error.Message}");
            return Failure;
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C: linhas já commitadas ficam; a em curso sofre rollback. Repetir é seguro (idempotente).
            await stderr.WriteLineAsync("cancelado");
            return Failure;
        }
        catch (FormatException error)
        {
            // Configuração malformada (p.ex. porta do broker não numérica): erro de uso, não falha de execução.
            await stderr.WriteLineAsync($"configuração inválida: {error.Message}");
            return UsageError;
        }
    }

    private static async Task<int> ReconcileAsync(
        Func<string, string?> config, TextWriter stdout, Guid merchant, DateOnly from, DateOnly to, bool rebuild, CancellationToken cancellationToken)
    {
        await using var consolidation = NpgsqlDataSource.Create(Required(config, DefaultKey));
        await using var source = NpgsqlDataSource.Create(Required(config, SourceKey));
        var report = await new Reconciler(consolidation, source, stdout).RunAsync(merchant, from, to, rebuild, cancellationToken);
        await stdout.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"total: modo={(rebuild ? "rebuild" : "reconcile")} linhas={report.Rows.Count} divergencias={report.Divergences} abortadas={report.Aborted} posse_lock_max_ms={report.MaxLockHeld.TotalMilliseconds:0}"));
        return report.Aborted > 0 ? Attention : Ok;
    }

    private static async Task<int> RedriveAsync(
        Func<string, string?> config, TextWriter stdout, TextWriter stderr, string? id, CancellationToken cancellationToken)
    {
        await using var consolidation = NpgsqlDataSource.Create(Required(config, DefaultKey));
        var report = await new DlqRedriver(consolidation, RabbitMqOptions.From(config), stdout).RunAsync(id, cancellationToken);
        if (report.Failed)
        {
            return Failure;
        }

        if (id is not null && !report.TargetFound)
        {
            await stderr.WriteLineAsync($"mensagem {id} não encontrada na {Topology.DeadLetterQueue}");
            return Attention;
        }

        return report.Refused > 0 ? Attention : Ok;
    }

    /// <summary>Motivo obrigatório da trilha: texto livre curto, sem quebra de linha.</summary>
    public const int MaxReasonLength = 500;

    private static bool ValidReason(string reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= MaxReasonLength && !reason.Any(char.IsControl);

    private static async Task<int> PurgeAsync(
        Func<string, string?> config, TextWriter stdout, TextWriter stderr, string? id, string reason, CancellationToken cancellationToken)
    {
        await using var consolidation = NpgsqlDataSource.Create(Required(config, DefaultKey));
        var report = await new DlqPurger(consolidation, RabbitMqOptions.From(config), stdout).RunAsync(id, reason, cancellationToken);
        if (id is not null && !report.TargetFound)
        {
            await stderr.WriteLineAsync($"mensagem {id} não encontrada na {Topology.DeadLetterQueue}");
            return Attention;
        }

        return Ok;
    }

    private static bool TryRange(string[] args, out Guid merchant, out DateOnly from, out DateOnly to, out string error)
    {
        merchant = Guid.Empty;
        from = to = default;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--merchant" or "--from" or "--to") || !values.TryAdd(args[i], args[i + 1]))
            {
                error = "argumentos inválidos: informe --merchant, --from e --to uma vez cada.";
                return false;
            }
        }

        if (!values.TryGetValue("--merchant", out var merchantText) || !Guid.TryParseExact(merchantText, "D", out merchant) || merchant == Guid.Empty)
        {
            error = "--merchant deve ser um UUID.";
            return false;
        }

        if (!values.TryGetValue("--from", out var fromText) || !TryDate(fromText, out from)
            || !values.TryGetValue("--to", out var toText) || !TryDate(toText, out to))
        {
            error = "--from e --to devem estar no formato yyyy-MM-dd.";
            return false;
        }

        if (from > to || to.DayNumber - from.DayNumber >= MaxRangeDays)
        {
            error = $"intervalo inválido: --from <= --to e no máximo {MaxRangeDays} dias.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Required(Func<string, string?> config, string key) =>
        config(key) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configuração obrigatória ausente: {key}.");
}
