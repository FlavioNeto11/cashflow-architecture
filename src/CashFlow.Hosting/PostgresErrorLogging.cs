using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CashFlow.Hosting;

/// <summary>
/// Reclassifica o log de falhas de banco do EF: erros que a aplicação trata como resultado de negócio (ex.: 55P03
/// "chave em processamento" e 23505 "chave já confirmada" da idempotência, mapeados para 409/replay) deixam de
/// aparecer como Error; qualquer outra falha continua em Error, com a exceção. O EF registra CommandError e
/// SaveChangesFailed em Debug (ver <see cref="Configure"/>) e este interceptor emite o nível certo. Nada é
/// silenciado: o predicado decide apenas o nível.
/// </summary>
public sealed class PostgresErrorLogInterceptor(Func<PostgresException, bool> isExpected, ILogger<PostgresErrorLogInterceptor> logger)
    : DbCommandInterceptor, ISaveChangesInterceptor
{
    public static void Configure(DbContextOptionsBuilder options, PostgresErrorLogInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ConfigureWarnings(w => w.Log(
            (RelationalEventId.CommandError, LogLevel.Debug),
            (CoreEventId.SaveChangesFailed, LogLevel.Debug)));
        options.AddInterceptors(interceptor);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Report(eventData.Exception);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Report(eventData.Exception);
        return Task.CompletedTask;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReportSaveChanges(eventData.Exception);
    }

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReportSaveChanges(eventData.Exception);
        return Task.CompletedTask;
    }

    private void Report(Exception exception)
    {
        if (Find(exception) is { } postgres && isExpected(postgres))
        {
            logger.LogDebug("Erro do Postgres {SqlState} ({Constraint}) tratado pela aplicação.", postgres.SqlState, postgres.ConstraintName);
            return;
        }

        logger.LogError(exception, "Falha ao executar comando no banco ({SqlState}).", Find(exception)?.SqlState ?? "sem SQLSTATE");
    }

    // A falha do comando já foi registrada em Report; aqui só falhas de SaveChanges sem comando (ex.: concorrência).
    private void ReportSaveChanges(Exception exception)
    {
        if (Find(exception) is null)
        {
            logger.LogError(exception, "Falha ao salvar alterações.");
        }
    }

    private static PostgresException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
