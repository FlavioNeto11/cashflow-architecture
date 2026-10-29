# Runbooks operacionais (F6) - ambiente local Docker Compose

**Escopo e honestidade.** Estes runbooks cobrem o ambiente **local** (Compose). Comandos marcados **[EXECUTADO]** foram rodados nesta fase
com saída guardada em `docs/testing/evidence/f6/`; **[NÃO EXECUTADO]** foram escritos a partir do código/configuração e nunca rodados.
Alertas citados são **PROPOSTA** (nenhum sistema de alertas existe; `deployment.md` seção 5). Nenhum número daqui é SLO medido.

| Runbook | Quando usar |
|---|---|
| [broker-indisponivel.md](broker-indisponivel.md) | RabbitMQ parado, pausado, bloqueado por alarme |
| [worker-parado-lag-consumo-pausado.md](worker-parado-lag-consumo-pausado.md) | Consolidado desatualizado, `/health/ready` do worker 503, sem consumidor |
| [dlq.md](dlq.md) | Mensagens em `consolidation.dlq` (triagem por `x-reason`/`x-death`), evento com mais de 29 dias, redrive |
| [reconciliacao-e-rebuild.md](reconciliacao-e-rebuild.md) | Divergência entre `transactions` e `daily_consolidations`; `reconcile`/`rebuild` |
| [rotacao-de-credenciais.md](rotacao-de-credenciais.md) | Troca de senhas do `.env` (PostgreSQL, RabbitMQ) e secrets de clientes do Keycloak (`keycloak-init`) |
| [certificados-e-revogacao.md](certificados-e-revogacao.md) | Rotação de certificados (demo), revogação por blocklist, curl Schannel |
| [idp-indisponivel.md](idp-indisponivel.md) | Keycloak fora: processo aquecido × a frio |
| [diagnostico-401-403-429.md](diagnostico-401-403-429.md) | Logs e métricas de `CashFlow.Security` |

## Convenções dos comandos

Da raiz do repositório, com a stack base + dev no ar. Variáveis de credenciais vêm do `.env` local **sem imprimir valores**:

```bash
C="docker compose -f infra/compose/docker-compose.yml -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env"
# NÃO faça `set -a; . infra/compose/.env` no shell que roda `docker compose`: variáveis exportadas vencem o --env-file (armadilha de rotacao-de-credenciais.md).
# Leia o .env só em subshell; o segredo vai ao curl por stdin (-K -), fora do argv do processo:
envval() ( set -a; . infra/compose/.env; set +a; printf '%s' "${!1}" )
MQ=http://127.0.0.1:15672/api
mq() ( set -a; . infra/compose/.env; set +a; printf 'user = "%s:%s"\n' "$RABBITMQ_OPS_USER" "$RABBITMQ_OPS_PASS" | curl -s -K - -H 'content-type: application/json' "$@" )
# senha de papel PostgreSQL sem aparecer no argv do host:  PGPASSWORD="$(envval CONS_WORKER_PASSWORD)" docker exec -i -e PGPASSWORD cashflow-postgres-1 psql ...
RECON="$C --profile tools run --rm recon-tool"
```

- **Oráculo de consumo:** `docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name` (a API de gestão e `messages_unacknowledged` podem
  atrasar ou mentir sobre "há consumidor"). **Oráculo de fila:** `rabbitmqctl -q list_queues name messages consumers`.
- Se o projeto Compose carregar o override de observabilidade, inclua `-f infra/compose/docker-compose.observability.yml` em `$C`.
- **Papéis de PostgreSQL nos diagnósticos:** `migrator` é dono/DDL (e tem `CONNECTION LIMIT 2`): usar só em emergência. Para ler `outbox_messages` use `tx_app`; para `reconciliation_runs` use `recon_writer`; `psql -h 127.0.0.1` dentro do container é `trust`. **[trocas de papel feitas em passada documental; NÃO reexecutadas]**
- O usuário da aplicação **não** acessa a UI/API de gestão (401 comprovado); a gestão exige `RABBITMQ_OPS_USER/PASS`.
- Nomes de container (`cashflow-rabbitmq-1`, ...) assumem o projeto `cashflow` e uma réplica por serviço.
