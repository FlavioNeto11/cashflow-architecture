namespace CashFlow.Transactions.Api.Security;

/// <summary>Escopos OAuth2 da API de lançamentos; cada um é também o nome da política de autorização.</summary>
public static class TransactionScopes
{
    public const string Write = "transactions.write";
    public const string Read = "transactions.read";
}
