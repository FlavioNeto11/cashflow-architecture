# Código de aplicação — scaffold F2 (sem regras de negócio)

Fundação .NET 10 (`net10.0`, SDK fixado em `global.json`). **Nada aqui implementa lançamentos, consolidado,
mensageria ou segurança**: são hosts que sobem, expõem saúde, falam com o banco e migram um esquema vazio.
Domínio de lançamentos: F3. Consolidação, Outbox/Inbox, broker e recon-tool reais: F4. Gateway, JWT e mTLS: F5.

## Projetos

| Projeto | Tipo | O que existe hoje |
|---|---|---|
| `CashFlow.Transactions.Api` | minimal API | Listener dual, `TransactionsDbContext` (vazio, migração `Initial`), `GET /api/v1` que só devolve o nome do serviço |
| `CashFlow.Consolidation.Api` | minimal API | Idem com `ConsolidationDbContext` |
| `CashFlow.Consolidation.Worker` | `BackgroundService` | `IdleWorker` só registra início/parada e respeita o `CancellationToken`; apenas o listener de saúde abre portas |
| `CashFlow.ReconTool` | console | `reconcile`, `rebuild`, `dlq redrive` imprimem "não implementado (F4)" e saem com código 2 (uso inválido: 64) |
| `CashFlow.Hosting` | biblioteca | Usada pelos 3 hosts: listener dual, health checks, OpenTelemetry/logs, `--healthcheck`; `--migrate` só nas duas APIs (o worker não migra) |
| `CashFlow.Consolidation.Persistence` | biblioteca | `ConsolidationDbContext` + migrações, compartilhado por `Consolidation.Api` e `Consolidation.Worker` |

Por que duas bibliotecas: `Hosting` evitaria copiar o mesmo código três vezes (APIs e worker); `Consolidation.Persistence`
existe porque dois processos usam o mesmo `DbContext`. Não há projeto de gateway nem biblioteca "BuildingBlocks".
`TransactionsDbContext` fica dentro da `Transactions.Api` porque só ela o usa.

## Convenções dos hosts

- **Listener dual (Kestrel).** Porta de negócio `8080` e porta de saúde `8081` (`Hosting:BusinessPort`, `Hosting:HealthPort`;
  `Hosting:BindAddress` padrão `127.0.0.1`, a imagem Docker usa `0.0.0.0`). A porta de saúde serve **somente**
  `/health/live` e `/health/ready` (HTTP puro); o resto responde 404. A porta de negócio responde 404 para `/health/*`.
  O isolamento usa a porta local da conexão (`ListenerIsolationMiddleware`), não o cabeçalho `Host`. mTLS na porta de negócio: F5.
- **`/health/live`** não consulta dependências. **`/health/ready`** das APIs consulta o PostgreSQL com o papel da própria
  aplicação (`tx_app`, `cons_ro`), pelo **mesmo** `NpgsqlDataSource` do `DbContext`, para o `Maximum Pool Size` valer
  para aquele pool valer para o processo. Há um data source por finalidade, com chave quando houver mais de um: `Default` (DbContext e health check) e, a partir de F3, `Publisher` (`AddCashFlowKeyedDataSource`, pool de 2, ainda sem uso em F2); o orçamento de conexões do papel soma todos os pools do processo. O worker não registra check de banco (só liveness; seu `/health/ready` responde 200 sem checks).
- **`--migrate`**: `dotnet <Host>.dll --migrate` aplica as migrações do contexto do host com `ConnectionStrings__Migrator`
  (papel `migrator`), não abre o Kestrel e sai com 0 (sucesso) ou 1 (falha ou variável ausente). A tabela de histórico do EF vive no schema `ef` (`ef."__EFMigrationsHistory"`), onde os papéis de aplicação não têm USAGE; o schema é criado pelo próprio EF.
- **`--healthcheck <url>`**: GET e saída 0/1. Existe porque as imagens .NET não trazem curl/wget.
- **Telemetria (decisão C-01).** Instrumentação OpenTelemetry de ASP.NET Core, HttpClient e Npgsql sempre registrada; o exportador
  OTLP só é ligado se `OTEL_EXPORTER_OTLP_ENDPOINT` estiver definido. Logs JSON no console com escopo `TraceId`/`SpanId`.
  Coletor e painel: F6.
- **Chave do Npgsql**: o limite do pool se escreve `Maximum Pool Size` (a forma curta `Max Pool Size` é rejeitada em runtime).
- **Connection strings**: não há valor padrão em `appsettings.json` (sem host nem usuário); `ConnectionStrings__Default` (e `__Publisher`, `__Migrator`) vêm do ambiente e a ausência derruba a partida. Em execução local fora do Compose, defina a variável.
- **Traces**: requisições `/health/*` são filtradas da instrumentação do ASP.NET Core.

## Como rodar

```bash
dotnet restore CashFlow.slnx
dotnet build CashFlow.slnx -c Release
dotnet test CashFlow.slnx -c Release          # exige Docker (Testcontainers sobe postgres:17)
dotnet format CashFlow.slnx --verify-no-changes
dotnet tool restore                            # dotnet-ef 10.0.14 (.config/dotnet-tools.json)
```

Nova migração (esquemas reais virão em F3/F4):

```bash
dotnet ef migrations add <Nome> --project src/CashFlow.Transactions.Api --output-dir Migrations
dotnet ef migrations add <Nome> --project src/CashFlow.Consolidation.Persistence --output-dir Migrations
```

Os contextos têm fábrica de design time com connection string marcador (`design-time`); nenhuma conexão é aberta.

## Pacotes (versões centralizadas em `Directory.Packages.props`)

EF Core 10.0.14, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, AspNetCore.HealthChecks.NpgSql 9.0.0 (resolvido sobre Npgsql 10),
OpenTelemetry 1.20.1 (+ Npgsql.OpenTelemetry 10.0.3). `CentralPackageTransitivePinningEnabled` fixa o EF Core Relational em
10.0.14 (sem isso o provider Npgsql traria 10.0.4 ao lado do EF Core 10.0.14 e o build emitiria MSB3277).
Sem MediatR, AutoMapper, FluentValidation, Serilog, RabbitMQ.Client ou YARP.
