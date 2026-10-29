namespace CashFlow.Transactions.Api.Domain;

/// <summary>Quem registrou o lançamento: claims sub e azp/client_id (não repúdio, ADR-0006).</summary>
public sealed record Author(string Sub, string ClientId);
