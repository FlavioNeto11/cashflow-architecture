using System.Globalization;
using System.Text.Json;
using CashFlow.Messaging;
using Npgsql;
using RabbitMQ.Client;

namespace CashFlow.ReconTool;

public sealed record RedriveReport(int Redriven, int Refused, int Skipped, bool Failed, bool TargetFound)
{
    public static RedriveReport Empty { get; } = new(0, 0, 0, false, false);
}

/// <summary>
/// Redrive da consolidation.dlq (ADR-0002 item 5): uma mensagem por vez por basic.get sem auto-ack. Para cada uma,
/// recusa o que está fora da retenção do Inbox (o worker voltaria a mandá-la à DLQ como stale, ou, se o Inbox já foi
/// expurgado, somaria em dobro), grava a trilha em reconciliation_runs ANTES de publicar, republica em
/// cashflow.transactions com a routing key original (mandatory + confirm) e só então dá ACK na DLQ.
/// Publicação não confirmada (return, nack, timeout) = sem ACK, a mensagem fica na DLQ, e a execução para.
/// Confirmado mas sem ACK (queda entre os dois) deixa a mensagem nas DUAS filas: um novo redrive gera uma
/// reentrega que o Inbox absorve (efeito idempotente; não é entrega exatamente uma vez).
/// </summary>
public sealed class DlqRedriver(NpgsqlDataSource consolidation, RabbitMqOptions options, TextWriter output)
{
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    public async Task<RedriveReport> RunAsync(string? onlyMessageId, CancellationToken cancellationToken)
    {
        var databaseNow = await DatabaseNowAsync(cancellationToken);
        var factory = options.CreateFactory("recon-tool-redrive");
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions, cancellationToken);

        // Mensagens puladas/recusadas ficam sem ACK neste canal: o broker não as entrega de novo a ele, então o laço
        // percorre a fila uma vez. Ao fechar o canal elas voltam à DLQ. "seen" evita republicar de novo uma mensagem
        // que o worker devolva à DLQ durante esta mesma execução (ela fica sem ACK e o laço segue até a fila esvaziar).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int redriven = 0, refused = 0, skipped = 0, looped = 0;
        var found = false;
        while (true)
        {
            var delivery = await channel.BasicGetAsync(Topology.DeadLetterQueue, autoAck: false, cancellationToken);
            if (delivery is null)
            {
                break;
            }

            var properties = delivery.BasicProperties;
            var messageId = properties.MessageId ?? string.Empty;
            if (!seen.Add(messageId.Length > 0 ? messageId : $"tag:{delivery.DeliveryTag}"))
            {
                looped++; // republicada e devolvida à DLQ nesta execução: fica sem ACK, não é republicada de novo
                continue;
            }

            if (onlyMessageId is not null && !string.Equals(messageId, onlyMessageId, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            found = true;
            var body = delivery.Body.ToArray();
            var facts = MessageFacts.Read(body, properties.Headers);
            var routingKey = AmqpHeaders.GetString(properties.Headers, HeaderNames.OriginalRoutingKey) ?? FirstDeathRoutingKey(properties.Headers);
            var trail = new
            {
                scope = "message",
                mode = "dlq_redrive",
                messageId,
                xReason = facts.Reason,
                xDeath = properties.Headers is { } h && h.TryGetValue("x-death", out var death) ? AmqpHeaders.ToPlain(death) : null,
                routingKey,
            };

            var refusal = facts switch
            {
                { Reason: DeadLetterReasons.Stale } => ("out_of_retention", "x-reason stale: usar reconcile/rebuild"),
                { Reason: DeadLetterReasons.ProjectionOverflow } => ("projection_overflow", "não transitório: exige rebuild/decisão sobre teto"),
                { OccurredAt: null } => ("anomaly", "occurredAt ilegível: retenção não verificável"),
                { OccurredAt: { } occurred } when occurred < databaseNow.AddDays(-InboxRetention.GuardDays) =>
                    ("out_of_retention", "fora da retenção do Inbox: usar reconcile/rebuild"),
                _ when routingKey is null => ("anomaly", "sem routing key original"),
                _ => default((string Kind, string Note)?),
            };

            if (refusal is { } refuse)
            {
                await RecordAsync(facts, databaseNow, refuse.Kind, new { trail, refused = refuse.Kind, note = refuse.Note }, cancellationToken);
                await output.WriteLineAsync($"{messageId} recusada {refuse.Kind}: {refuse.Note}");
                refused++;
                continue;
            }

            // Trilha ANTES de publicar: uma queda depois do confirm não deixa republicação sem registro.
            await RecordAsync(facts, databaseNow, "dlq_redrive", trail, cancellationToken);

            var republished = new BasicProperties(properties) { Headers = CleanHeaders(properties.Headers) };
            var outcome = await ConfirmedPublisher.PublishAsync(
                channel, new PublicationAddress(ExchangeType.Topic, Topology.TransactionsExchange, routingKey!), republished, body,
                ConfirmTimeout, cancellationToken);
            if (outcome != PublishOutcome.Confirmed)
            {
                await RecordAsync(facts, databaseNow, "redrive_failed", new { trail, outcome = outcome.ToString() }, CancellationToken.None);
                await output.WriteLineAsync($"{messageId} não confirmada ({outcome}); sem ACK, permanece na DLQ");
                return new RedriveReport(redriven, refused, skipped, Failed: true, found);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            await output.WriteLineAsync($"{messageId} republicada em {Topology.TransactionsExchange} ({routingKey}) e removida da DLQ");
            redriven++;
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture, $"redrive: republicadas={redriven} recusadas={refused} ignoradas={skipped} devolvidas_nesta_execucao={looped}"));
        return new RedriveReport(redriven, refused, skipped, Failed: false, found);
    }

    /// <summary>Sem x-*: o evento volta "limpo" (contagem de entregas, motivo e x-death são da passagem anterior).</summary>
    public static Dictionary<string, object?>? CleanHeaders(IDictionary<string, object?>? headers) =>
        headers?.Where(pair => !pair.Key.StartsWith("x-", StringComparison.Ordinal) && !HeaderNames.StrippedOnRedrive.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Mensagens mortas pelo próprio broker (x-delivery-limit) não têm x-original-routing-key: vale o x-death.</summary>
    internal static string? FirstDeathRoutingKey(IDictionary<string, object?>? headers) =>
        headers is not null && headers.TryGetValue("x-death", out var death)
            && AmqpHeaders.ToPlain(death) is List<object?> { Count: > 0 } deaths
            && deaths[0] is Dictionary<string, object?> first
            && first.TryGetValue("routing-keys", out var keys) && keys is List<object?> { Count: > 0 } list
            ? list[0] as string
            : null;

    private Task<DateTime> DatabaseNowAsync(CancellationToken cancellationToken) => DatabaseNowAsync(consolidation, cancellationToken);

    internal static async Task<DateTime> DatabaseNowAsync(NpgsqlDataSource consolidation, CancellationToken cancellationToken)
    {
        // Mesmo relógio da guarda do worker e do reconcile (now() do consolidation_db), não o da máquina do operador.
        await using var command = consolidation.CreateCommand("SELECT now()");
        return (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task RecordAsync(MessageFacts facts, DateTime databaseNow, string kind, object details, CancellationToken cancellationToken)
    {
        var date = facts.EffectiveDate ?? DateOnly.FromDateTime(databaseNow);
        await using var connection = await consolidation.OpenConnectionAsync(cancellationToken);
        await RunsWriter.InsertAsync(
            connection, null, facts.MerchantId ?? Guid.Empty, date, date, DateTime.UtcNow, kind == "dlq_redrive" ? 1 : 0,
            new Dictionary<string, int>(StringComparer.Ordinal) { [kind] = 1 }, details, cancellationToken);
    }

    /// <summary>Leitura tolerante (a mensagem pode estar na DLQ justamente por ser inválida); nada de valores em log.</summary>
    internal sealed record MessageFacts(string? Reason, DateTime? OccurredAt, Guid? MerchantId, DateOnly? EffectiveDate)
    {
        public static MessageFacts Read(byte[] body, IDictionary<string, object?>? headers)
        {
            var reason = AmqpHeaders.GetString(headers, HeaderNames.Reason) ?? AmqpHeaders.GetString(headers, "x-first-death-reason");
            Guid? merchant = Guid.TryParseExact(AmqpHeaders.GetString(headers, HeaderNames.MerchantId), "D", out var fromHeader) ? fromHeader : null;
            DateTime? occurred = null;
            DateOnly? date = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (Text(root, "occurredAt") is { } occurredText
                        && DateTimeOffset.TryParse(occurredText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        occurred = parsed.UtcDateTime;
                    }

                    if (Text(root, "merchantId") is { } merchantText && Guid.TryParseExact(merchantText, "D", out var fromBody))
                    {
                        merchant = fromBody;
                    }

                    if (Text(root, "effectiveDate") is { } dateText
                        && DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    {
                        date = parsedDate;
                    }
                }
            }
            catch (JsonException)
            {
                // payload ilegível: OccurredAt nulo => recusa como anomalia
            }

            return new MessageFacts(reason, occurred, merchant, date);
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
