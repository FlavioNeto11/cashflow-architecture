using System.Globalization;
using System.Security.Cryptography;
using CashFlow.Messaging;
using Npgsql;
using RabbitMQ.Client;

namespace CashFlow.ReconTool;

public sealed record PurgeReport(int Purged, int Skipped, bool TargetFound);

/// <summary>
/// Descarte auditado da consolidation.dlq (PF-09): substitui o purge pela API de gerenciamento, que não deixa rastro.
/// Uma mensagem por vez por basic.get sem auto-ack; para cada uma grava em reconciliation_runs (modo dlq_purge) o
/// message-id, x-reason, routing key, merchant, data efetiva, SHA-256 do corpo e o motivo do operador, e só então dá
/// ACK. Queda entre a trilha e o ACK deixa a mensagem na DLQ com a trilha gravada: repetir gera uma segunda linha
/// para o mesmo message-id, nunca descarte sem registro. Descartar não corrige o consolidado: o lançamento continua
/// na fonte e o caminho de correção é o reconcile do merchant/data registrados na trilha.
/// </summary>
public sealed class DlqPurger(NpgsqlDataSource consolidation, RabbitMqOptions options, TextWriter output)
{
    public async Task<PurgeReport> RunAsync(string? onlyMessageId, string reason, CancellationToken cancellationToken)
    {
        var databaseNow = await DlqRedriver.DatabaseNowAsync(consolidation, cancellationToken);
        var factory = options.CreateFactory("recon-tool-purge");
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        // Mensagens puladas ficam sem ACK neste canal e voltam à DLQ quando ele fecha (mesmo laço do redrive). A DLQ é
        // clássica e sem x-delivery-limit (infra/rabbitmq/definitions.json): voltar sem ACK não a faz descartar nada.
        int purged = 0, skipped = 0;
        var found = false;
        while (await channel.BasicGetAsync(Topology.DeadLetterQueue, autoAck: false, cancellationToken) is { } delivery)
        {
            var properties = delivery.BasicProperties;
            var messageId = properties.MessageId ?? string.Empty;
            if (onlyMessageId is not null && !string.Equals(messageId, onlyMessageId, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            found = true;
            var body = delivery.Body.ToArray();
            var facts = DlqRedriver.MessageFacts.Read(body, properties.Headers);
            var date = facts.EffectiveDate ?? DateOnly.FromDateTime(databaseNow);
            var trail = new
            {
                scope = "message",
                mode = "dlq_purge",
                messageId,
                xReason = facts.Reason,
                routingKey = AmqpHeaders.GetString(properties.Headers, HeaderNames.OriginalRoutingKey)
                    ?? DlqRedriver.FirstDeathRoutingKey(properties.Headers),
                effectiveDate = facts.EffectiveDate,
                bodySha256 = Convert.ToHexStringLower(SHA256.HashData(body)),
                operatorReason = reason,
            };

            // Trilha ANTES do ACK: nenhuma mensagem sai da DLQ sem registro.
            await using (var db = await consolidation.OpenConnectionAsync(cancellationToken))
            {
                await RunsWriter.InsertAsync(
                    db, null, facts.MerchantId ?? Guid.Empty, date, date, DateTime.UtcNow, 0,
                    new Dictionary<string, int>(StringComparer.Ordinal) { ["dlq_purge"] = 1 }, trail, cancellationToken);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            await output.WriteLineAsync($"{messageId} descartada da {Topology.DeadLetterQueue} (trilha gravada; corrigir com reconcile)");
            purged++;
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"purge: descartadas={purged} ignoradas={skipped}"));
        return new PurgeReport(purged, skipped, found);
    }
}
