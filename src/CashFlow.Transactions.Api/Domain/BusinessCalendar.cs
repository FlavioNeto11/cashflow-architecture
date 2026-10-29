namespace CashFlow.Transactions.Api.Domain;

/// <summary>
/// Data de competência (effective_date) é a data civil no fuso de referência America/Sao_Paulo (P-03),
/// não a data UTC. Em Linux depende de tzdata (presente em mcr.microsoft.com/dotnet/aspnet:10.0).
/// </summary>
public static class BusinessCalendar
{
    public const string ZoneId = "America/Sao_Paulo";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    public static DateOnly DateOf(DateTime utcInstant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), Zone));
}
