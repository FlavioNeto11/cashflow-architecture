# Arquitetura implementada (estado real)

Última conferência: **F9, 2026-10-29** (correções pós-G2 e reexecuções com as imagens atuais), com base no código em `src/`, em `infra/compose/` e nos logs de `docs/testing/evidence/`. O desenho-alvo continua em `target.md`, `security.md` e `deployment.md`; este documento diz o que **existe e roda**, o que foi **validado** e com qual evidência, e o que ficou **projetado ou futuro**. Tudo vale para **uma máquina local com Docker Desktop**; nada foi implantado em nuvem e nenhuma medição vale para produção. O aceite final (G2) foi dado pelo responsável em 2026-10-29 (`docs/project/approvals.md`).

Rótulos usados: **VALIDADO** (executado com oráculo e log), **PARCIAL** (executado em parte ou com ressalva), **IMPLEMENTADO, NÃO VALIDADO** (código existe, sem teste na stack), **PROJETADO** (só desenho aprovado), **FUTURO** (fora do MVP). Caminhos `evidence/...` são relativos a `docs/testing/`.

## 1. Processos realmente executáveis

Projeto Compose `cashflow` (`infra/compose/docker-compose.yml`). Redes: `edge` (só o gateway), `app` (gateway, APIs, worker, IdP) e `data` (PostgreSQL, RabbitMQ e quem precisa deles).

| Serviço | Imagem | Papel | Porta no host |
|---|---|---|---|
| `gateway` | `cashflow/gateway:dev` (YARP) | Única entrada das APIs: TLS, validação de JWT, rate limit, mTLS para as APIs | `127.0.0.1:8443` |
| `transactions-api` | `cashflow/transactions-api:dev` | Lançamentos, idempotência, Outbox transacional | nenhuma |
| `consolidation-worker` | `cashflow/consolidation-worker:dev` | Consome eventos, Inbox, projeção diária | nenhuma |
| `consolidation-api` | `cashflow/consolidation-api:dev` | Leitura do consolidado diário | nenhuma |
| `recon-tool` | `cashflow/recon-tool:dev` (perfil `tools`, sob demanda) | `reconcile`, `rebuild`, `dlq redrive`, `dlq purge` (com trilha) | nenhuma |
| `migrations-tx`, `migrations-cons` | imagens das APIs | Jobs one-shot de migração (papel `migrator`) | nenhuma |
| `postgres` | `postgres:17` por digest | Um servidor, dois bancos (`transactions_db`, `consolidation_db`), papéis com `CONNECTION LIMIT` | nenhuma |
| `rabbitmq` | `rabbitmq:4-management` por digest | Exchange `cashflow.transactions`, fila `consolidation.transaction-registered`, DLX e DLQ | `127.0.0.1:15672` (UI) |
| `keycloak` + `keycloak-init` | `keycloak:26.8.1` por digest, `start-dev` | IdP OIDC (realm `cashflow`; clientes confidenciais `merchant-a-system` e `merchant-b-system`; cliente público `cashflow-web` com PKCE S256 e usuários de demonstração `demo-a` e `demo-b`, `infra/keycloak/realm-export.json`) | `127.0.0.1:8080` |

Invariante de RNF-01 conferida no arquivo: `transactions-api` depende só de `migrations-tx` (e, por ele, do PostgreSQL); não há `depends_on` de RabbitMQ nem do lado da consolidação. Overrides: `docker-compose.dev.yml` (chamadas diretas sem token, só para desenvolvimento) e `docker-compose.observability.yml` (coletor OTLP).

## 2. Endpoints disponibilizados (via gateway, `https://127.0.0.1:8443/api/v1`)

| Método e caminho | Escopo exigido | Origem |
|---|---|---|
| `POST /merchants/{merchantId}/transactions` (cabeçalho `Idempotency-Key` obrigatório) | `transactions.write` | `src/CashFlow.Transactions.Api/Http/TransactionEndpoints.cs:18` |
| `POST /merchants/{merchantId}/transactions/{id}/reversal` | `transactions.write` | `TransactionEndpoints.cs:19` |
| `GET /merchants/{merchantId}/transactions` (filtro por data, paginado, máx. 200 por página) | `transactions.read` | `TransactionEndpoints.cs:20` |
| `GET /merchants/{merchantId}/transactions/{id}` | `transactions.read` | `TransactionEndpoints.cs:21` |
| `GET /merchants/{merchantId}/consolidations/daily?date=...[&include=closingBalance]` | `consolidation.read` | `src/CashFlow.Consolidation.Api/Http/DailyConsolidationEndpoints.cs:47` |

O `merchantId` da rota precisa ser igual à claim `merchant_id` do token; caso contrário a resposta é `404` (não `403`), para não revelar a existência de outro tenant. Saúde (`/health/*`) fica numa porta separada (8081), fora do gateway e não publicada.

## 3. Controles realmente habilitados, canal a canal

| Canal | Proteção real | Estado |
|---|---|---|
| Cliente → gateway | TLS unilateral com a CA local (`infra/pki/out/ca.crt`); JWT Bearer validado (issuer exato, `aud=cashflow-api`, algoritmos padrão RS256/ES256, lista permitida RS*/PS*/ES*); rate limit por merchant (60/s no consolidado) e teto global (120/s) só para autenticados, encadeados: a requisição recusada pelo limite do merchant não consome o teto global | VALIDADO com as imagens atuais (`evidence/f9/15b-rerun-02-security.log`, 87 OK, 0 FALHA; `evidence/f7/06-t08-probe-*.log`). Isolamento entre merchants no limiter: falhava em F7 (PF-18); corrigido em F9: teste determinístico na suíte (`MerchantBurstGlobalLimiterTests`, `evidence/f9/04-pf18-depois.log`) e, na stack, 0 respostas 429 para o merchant B em 2 rodadas com rajadas do merchant A (`evidence/f9/20-pf18-stack-b1.log`, `20-pf18-stack-b2.log`) |
| Gateway → APIs | **mTLS**: certificado de cliente obrigatório da CA local, EKU `clientAuth`, SAN `gateway.internal`, blocklist de thumbprints relida a quente; as APIs revalidam o JWT por conta própria | VALIDADO para os negativos básicos (sem certificado, CA estranha, HTTP puro); expirado, SAN, EKU e blocklist só na suíte (`tests/CashFlow.Security.Tests/MutualTlsTests.cs`, execução em `evidence/f7/21-02-suite.log`) |
| APIs e gateway → Keycloak (JWKS) | HTTP dentro da rede `app` (`AllowHttpMetadata=true`, aviso na partida) | GAP declarado (`security.md` seção 11) |
| Serviços → PostgreSQL | Sem TLS, rede `data`; um papel por serviço com privilégio mínimo e `CONNECTION LIMIT` | Privilégios validados em F6 (`docs/testing/evidence-log.md`); TLS FUTURO |
| Serviços → RabbitMQ | AMQP sem TLS, rede `data`; **um único** usuário de aplicação com a união das permissões (C-22) | Desvio aceito; o alvo é um usuário por serviço |
| Serviços → coletor OTLP | Sem TLS, só com o override de observabilidade | PARCIAL |
| Listener de negócio sem certificado fora de Development | **Fail-closed** (C-F5-09): a partida falha sem `Hosting:Tls:ServerCertificate`, salvo opt-in explícito `Hosting:Tls:AllowPlainHttp=true` (só testes e perfil dev usam) | VALIDADO na suíte (`AuthenticationModeTests`, `evidence/f9/06-suite-transactions-api.log`) |

## 4. Alvo × implementado × futuro

| Item | Estado | Evidência ou nota |
|---|---|---|
| Três processos, Outbox e Inbox (opção C, G1-03) | VALIDADO com as imagens atuais | `evidence/f9/17-rerun-07-resilience.log`, `evidence/f9/18-rerun-08-dlq.log` (FALHAS=0 nos dois) |
| POST com toda a consolidação parada (RNF-01) | VALIDADO com as imagens atuais | `evidence/f9/17-rerun-07-resilience.log` R1: 200 POSTs, todos 201 |
| Só o `consolidation_db` fora (T-04c) | NÃO COBERTO | `docs/testing/test-plan.md` |
| Broker fora: Outbox acumula e drena | VALIDADO com as imagens atuais | `evidence/f9/17-rerun-07-resilience.log` R2: 300 POSTs, todos 201, 300 linhas e 300 no Inbox; a medição de tempo de drenagem citada antes (outbox zerada 13,6 s após o broker ficar healthy) é de `evidence/f7/07-resilience.log` |
| Idempotência HTTP, inclusive concorrente e com falha ambígua | VALIDADO com as imagens atuais | `evidence/f9/16-rerun-03-idempotency.log` (0 FALHA); `evidence/f7/21-06-ambiguous-failure.log` (45 tentativas, sempre 1 linha) |
| Morte do processo (SIGKILL) sem duplicidade | VALIDADO com as imagens atuais | `evidence/f9/17-rerun-07-resilience.log`: R3 (worker, 250 linhas), R4a (transactions-api: 297 respostas 201 e 3 respostas 502 durante a queda; o reenvio com a mesma chave fechou 300 linhas, nenhuma duplicada) e R4b (gateway, 250 linhas); PF-14 fechado |
| Estorno como lançamento inverso | VALIDADO com as imagens atuais | `evidence/f9/16-rerun-03-idempotency.log` seção 5; estorno antes do original na projeção (T-22) NÃO COBERTO |
| Retroativo e `closingBalance` | VALIDADO (imagem de F4) | `evidence/f4/verify-compose-scenarios-round4.log:42-69` |
| Reconciliação, correção idempotente e rebuild (G1-10, opção b) | VALIDADO (imagem de F4) | `round4.log:239-295`; T-14b e `source_timeout` 57014 NÃO COBERTOS |
| DLQ, redrive e purge | VALIDADO com as imagens atuais | `evidence/f9/18-rerun-08-dlq.log` D1–D5; o purge passou a ser feito pelo `recon-tool dlq purge`, que grava uma trilha por mensagem em `reconciliation_runs` antes do ACK (PF-09 corrigido; D5: DLQ 3 → 2 → 0, 3 trilhas; suíte em `evidence/f9/05-pf09-purge.log`) |
| JWT, escopo e tenant → 404 | VALIDADO com as imagens atuais | `evidence/f9/15b-rerun-02-security.log` (87 OK, 0 FALHA) |
| `merchant_id` não editável pelo usuário | PARCIAL | Configuração conferida pela API de administração (`evidence/f7/22-keycloak-profile.log`); edição interativa não tentada |
| Token sem a claim `merchant_id` | VALIDADO com as imagens atuais | `evidence/f9/13-token-sem-merchant.log`: client temporário sem o mapper; POST e GETs recusados com 403, nenhum lançamento gravado; client removido |
| IdP fora com tokens já emitidos | VALIDADO (janela curta) | `evidence/f7/23-idp-outage.log` |
| Cold start com IdP fora | PROJETADO | G1-15 declara o GAP |
| PKCE (cliente público `cashflow-web`, S256) | IMPLEMENTADO, NÃO VALIDADO | Configurado no realm; o fluxo de autorização com navegador nunca foi exercitado |
| RLS no PostgreSQL | FUTURO | `security.md` |
| CRL/OCSP; TLS em banco, broker, IdP e OTLP | FUTURO | `security.md` seção 11 |
| Rate limit por merchant | VALIDADO | `evidence/f7/06-t08-probe-single-80.log` (22,79% de 429 a 80/s); `06-t08-probe-two-130.log`; isolamento entre merchants (PF-18) corrigido em F9, ver seção 3 |
| Rate limit distribuído entre instâncias | FUTURO | Hoje em memória por instância; os limites se multiplicam ao escalar |
| 50 RPS de GET do consolidado, perda ≤ 5% (fórmula G1-07) | VALIDADO localmente, com 1 e com 10 tenants | `evidence/f7/21-05-base-get50-120s.log` (1 token, 6001 req, 0,00%, p95 6,88 ms, p99 9,93 ms); `06-t08-get-two-tokens.log` (2 tokens, 6001 req, 0,00%) e `06-t08-get-single-10min.log` (30001 req), ambos anteriores ao rebuild; 10 tenants em `evidence/f9/14-t08-10tenants.log` |
| Perfil de 10 tenants do ADR-0009 | VALIDADO localmente | `evidence/f9/14-t08-10tenants.log`: 10 tokens (os 2 clients do realm e 8 temporários, removidos ao fim), 6001 GETs a 50,01/s, 0,00% de perda, p95 7,09 ms e p99 10,82 ms; 600 ou 601 respostas corretas por merchant, conferidas contra o oráculo SQL de cada um |
| Logs estruturados e OTLP | PARCIAL | Só com o override de observabilidade |
| Métricas de lag, idade da DLQ e da outbox; alertas | FUTURO | `deployment.md` seção 5; T-11 NÃO COBERTO |
| Expurgo do Inbox (30 dias, G1-13), da outbox publicada e das chaves de idempotência | PROJETADO | Não há job de expurgo (C-15, `deployment.md`): existem só os limiares em `InboxRetention.cs` e a guarda de 29 dias usada por `reconcile` e pelo redrive. As tabelas crescem sem limite no MVP (I6) |
| Scan de imagens | Executado em F7 e reexecutado em F9 | `evidence/f7/12-trivy-imagens.log` (F7); `evidence/f9/23-trivy-imagens.log` (imagens atuais) |
| CI de build e teste | VALIDADO | `.github/workflows/ci.yml` (GitHub Actions, `ubuntu-latest`): build Release com avisos como erro e as 4 suítes (364 testes, Testcontainers); a stack Compose e o k6 não rodam no CI. Execução em `evidence/f9/21-ci-github-actions.log` |
| Repositório público | Publicado | `https://github.com/FlavioNeto11/cashflow-architecture` |
| Linux nativo | PARCIAL | `make-certs.sh` validado em container Linux (`evidence/f7/19-make-certs-linux.log`), não em host |
| Backup, restore, RTO/RPO medidos | PROJETADO | `deployment.md` seção 8 |
| Deploy em nuvem e custos | PROJETADO | `deployment.md`, `cost-estimate.md` (estimativa) |

## 5. Como foi verificado

- Build e testes: `dotnet build CashFlow.slnx -c Release -warnaserror` e `dotnet test CashFlow.slnx -c Release --no-build`, com 364 testes aprovados (175 + 71 + 69 + 49) em `evidence/f9/06-suite-transactions-api.log` a `09-suite-persistence.log` e no CI (`evidence/f9/21-ci-github-actions.log`); a contagem de F7 (352) está em `evidence/f7/21-02-suite.log`; formatação em `evidence/f7/14-build-format.log`.
- Stack: cenários de F4, F6 e F7 com oráculo SQL, listados com comandos e resultados em `docs/testing/evidence-log.md`.
- Carga: k6 em container `grafana/k6` fixado por digest na rede `cashflow_app` (o k6 não está instalado no host).
- Quickstart do `README.md` da raiz: ver `evidence/f8/01-readme-quickstart.log`.

## 6. Limitações em relação a `target.md`

As lacunas em aberto, com severidade, estão em `docs/project/final-audit.md` e os riscos em `docs/project/risk-register.csv`. As mais relevantes para a banca: rate limit em memória por instância, usuário AMQP único (C-22), observabilidade sem métricas de lag, idade da DLQ e da outbox, ausência de job de expurgo do Inbox, da outbox e das chaves de idempotência (C-15, I6), host Linux nativo não validado, CVEs de imagens de terceiros e os casos NÃO COBERTOS de `docs/testing/test-plan.md`. PF-17, PF-18, PF-09, PF-14, C-F5-09, CI, 10 tenants e as reexecuções com as imagens atuais foram tratados em F9.
