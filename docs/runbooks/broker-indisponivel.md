# Runbook: broker (RabbitMQ) indisponível ou pausado

**Garantia a preservar (RNF-01/ADR-0002):** o `POST /transactions` continua devolvendo 201 sem broker; o evento fica no **outbox** (mesma transação do
lançamento) e é publicado quando o broker volta. Nada de lançamento confirmado se perde. O consolidado fica **desatualizado**, não incorreto.

## Sintomas
- `docker compose ps`: `rabbitmq` fora ou `unhealthy`; worker com `/health/ready` 503 após 60 s sem consumidor (C-21).
- Outbox crescendo: `outbox_pending > 0` e `outbox_oldest_age_seconds` subindo (métricas OTLP quando o override de observabilidade está ativo).
- Logs da `transactions-api`/`consolidation-worker`: "Broker ... indisponível ...; reconectando com backoff".
- **PROPOSTA de alerta:** `outbox_pending > 100` ou `outbox_oldest_age_seconds > 60` (HIPÓTESES, `deployment.md` 5).

## Diagnóstico
```bash
docker inspect -f '{{.State.Status}} {{.State.Paused}} {{.State.Health.Status}}' cashflow-rabbitmq-1      # [EXECUTADO em F4/F6 em partes]
docker exec cashflow-rabbitmq-1 rabbitmq-diagnostics -q ping                                                # [NÃO EXECUTADO isolado]
docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name                                    # [EXECUTADO]
docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name messages consumers                          # [EXECUTADO]
# outbox pendente (papel tx_app: DML sem DDL; o migrator é dono e só em emergência)  [papel trocado em passada documental; NÃO reexecutado, o log 12 usou migrator]:
docker exec -i cashflow-postgres-1 psql -U tx_app -d transactions_db -At -c \
 "SELECT count(*) FILTER (WHERE published_at IS NULL) AS pendentes, coalesce(round(extract(epoch FROM now()-min(occurred_at) FILTER (WHERE published_at IS NULL))),0) AS idade_s FROM outbox_messages"   # [EXECUTADO]
```
Alarmes do broker (disco/memória; `connection.blocked`): `docker exec cashflow-rabbitmq-1 rabbitmq-diagnostics -q alarms` **[NÃO EXECUTADO]**.
Lacuna conhecida: o publisher **não trata `connection.blocked`** (documentado para F5/F7; sob alarme de memória/disco o publish pode travar até o timeout de confirmação).

## Ação
1. Parado: `$C start rabbitmq` (ou `docker start cashflow-rabbitmq-1`). Pausado (`docker pause`): `$C unpause rabbitmq` **[unpause NÃO EXECUTADO; F4 exercitou pause/unpause]**.
2. **Não** reiniciar `transactions-api` nem apagar o volume `rabbitmq-data`: o outbox reenvia sozinho e a topologia é recriada pelo `definitions.json`.
3. Aguardar: consumidor volta (`list_consumers` = 1), `/health/ready` do worker (`127.0.0.1:18084` no perfil dev) = 200, `outbox_pending` → 0.
4. Se o volume do broker foi perdido: a topologia volta pelas definições; mensagens publicadas e não consumidas se perdem e **não** voltam do outbox
   (já marcadas publicadas) → rodar `reconcile` do intervalo afetado (ver `reconciliacao-e-rebuild.md`).

## Execução real (F6) - `docs/testing/evidence/f6/12-runbook-broker-down.log`
Roteiro `f6-runbook-broker-down.sh`: `docker stop` do broker, POST (201), `/health/ready` da API, 70 s depois o `ready` do worker, `start`, drenagem. Resultado no log.

## Duas situações diferentes (não confundir)
1. **Broker pausado/parado** (`docker pause`/`stop`): conexões ficam sem resposta até o heartbeat/timeout dos clientes; o publisher mantém o outbox e reconecta com backoff. **`consumer_timeout` não está envolvido.** F4 exercitou pause/unpause; o intervalo exato até a queda das conexões não foi medido em F6.
2. **Worker segurando entrega sem ACK** (pausa de consumo por banco do consolidado fora): se a entrega ficar sem ACK além de 30 min (`consumer_timeout`, `infra/rabbitmq/rabbitmq.conf`), o **broker** fecha o canal; o worker reconecta e a mensagem volta à fila. Limite declarado (G1-16); rede de segurança: DLQ + reconciliação.
