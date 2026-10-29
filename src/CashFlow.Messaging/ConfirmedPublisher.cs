using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CashFlow.Messaging;

public enum PublishOutcome
{
    /// <summary>O broker confirmou (publisher confirm).</summary>
    Confirmed,

    /// <summary>basic.return de uma publicação mandatory: nenhuma fila recebeu (sem rota).</summary>
    Returned,

    /// <summary>basic.nack do broker.</summary>
    Nacked,

    /// <summary>Sem confirmação dentro do prazo: resultado desconhecido, tratado como falha (pode ter chegado).</summary>
    TimedOut,
}

/// <summary>
/// Publicação mandatory + persistente com espera do publisher confirm (RabbitMQ.Client 7: canal criado com
/// confirmações e rastreamento habilitados; o BasicPublishAsync só retorna após o ack do broker, e um basic.return
/// vira PublishException com IsReturn). Erros de canal/conexão propagam: quem chama trata como indisponibilidade.
/// </summary>
public static class ConfirmedPublisher
{
    public static CreateChannelOptions ChannelOptions { get; } = new(
        publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    public static async Task<PublishOutcome> PublishAsync(
        IChannel channel, PublicationAddress address, BasicProperties properties, ReadOnlyMemory<byte> body,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(properties);
        properties.Persistent = true;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await channel.BasicPublishAsync(address.ExchangeName, address.RoutingKey, mandatory: true, properties, body, deadline.Token);
            return PublishOutcome.Confirmed;
        }
        catch (PublishException exception) when (exception.IsReturn)
        {
            return PublishOutcome.Returned;
        }
        catch (PublishException)
        {
            return PublishOutcome.Nacked;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PublishOutcome.TimedOut;
        }
    }
}
