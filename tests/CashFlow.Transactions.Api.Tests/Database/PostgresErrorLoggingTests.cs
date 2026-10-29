using CashFlow.Transactions.Api.Tests.Api;
using CashFlow.Transactions.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CashFlow.Transactions.Api.Tests.Database;

/// <summary>
/// Reclassificação do log de erros do banco (PostgresErrorLogInterceptor): o 23505 da chave de idempotência, que a
/// aplicação trata como replay/409, não gera log Error; um erro real (42501, sem privilégio) continua em Error.
/// Os comandos passam pelo MESMO DbContext/pipeline do EF configurado no Program.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PostgresErrorLoggingTests(ApiFixture fixture)
{
    private const string InsertKey = """
        INSERT INTO idempotency_keys (merchant_id, idempotency_key, request_hash, transaction_id, response_status,
                                      response_body, created_at, expires_at)
        VALUES ({0}, 'k-log-test', repeat('a', 64), {1}, 201, '{{}}'::jsonb, now(), now() + interval '1 day')
        """;

    [Fact]
    public async Task HandledUniqueViolation_IsNotLoggedAsError_ButAPrivilegeErrorIs()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new TransactionsApiFactory(fixture.TxAppConnectionString, fixture.TxAppConnectionString)
        {
            LogProvider = logs,
        };

        // O nível Debug do interceptor precisa estar habilitado para provar que o 23505 foi visto e rebaixado.
        await using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Logging:LogLevel:CashFlow.Hosting.PostgresErrorLogInterceptor", "Debug"));
        using var scope = configured.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TransactionsDbContext>();
        var merchant = Guid.CreateVersion7();

        await db.Database.ExecuteSqlRawAsync(InsertKey, merchant, Guid.CreateVersion7());
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(InsertKey, merchant, Guid.CreateVersion7()));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.DoesNotContain(logs.Messages, m => m.Level >= LogLevel.Error);
        Assert.Contains(logs.Messages, m => m.Level == LogLevel.Debug && m.Category.EndsWith("PostgresErrorLogInterceptor", StringComparison.Ordinal) && m.Text.Contains("23505", StringComparison.Ordinal));

        var denied = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM outbox_messages WHERE false"));
        Assert.Equal("42501", denied.SqlState);
        Assert.Contains(logs.Messages, m => m.Level == LogLevel.Error && m.Category.EndsWith("PostgresErrorLogInterceptor", StringComparison.Ordinal) && m.Text.Contains("42501", StringComparison.Ordinal));
    }
}
