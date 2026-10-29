# Runbook: worker parado, lag alto ou consumo pausado

**Garantia:** a queda do consolidado/worker **não** derruba o registro de lançamentos (RNF-01). O consolidado fica atrasado; eventos esperam na fila
`consolidation.transaction-registered` (quorum, durável) e são aplicados com Inbox idempotente (sem dupla contabilização).

## Sintomas
- `GET .../consolidations/daily` com `asOf` antigo ou `dailyNet` abaixo do esperado.
- `/health/ready` do worker = **503** após 60 s sem consumidor registrado (C-21; `Consolidation:Worker:NotConsumingUnhealthySeconds`).
  `/health/live` continua 200 de propósito (liveness não reinicia por dependência externa). **Atenção:** o Compose **não reinicia** container `unhealthy`.
- Métrica `consolidation_lag_seconds` alto. A `consumer_paused_seconds` é somada **só ao FIM da pausa** (`ConsolidationConsumer.cs:252-256`): ela **não cresce durante** a pausa e não serve de sinal em tempo real.
- **PROPOSTA de alerta:** `ready` do worker = 503 por > 60 s ou 0 consumidores na fila; `consolidation_lag_seconds` p95 > 60 s; fila com `messages_ready` crescente e 0 consumidores. (`consumer_paused_seconds` > 60 s só detecta a pausa depois que ela termina.)

## Diagnóstico (nesta ordem)
```bash
# 1. há consumidor? (ORÁCULO: rabbitmqctl, não a API de gestão)                                         [EXECUTADO]
docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name
docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name messages consumers                    # [EXECUTADO]
# 2. estado do worker                                                                                    [EXECUTADO]
docker inspect -f '{{.State.Status}} {{.State.Health.Status}}' cashflow-consolidation-worker-1
curl -s -o /dev/null -w 'ready=%{http_code}\n' http://127.0.0.1:18084/health/ready                     # perfil dev publica 18084
$C logs --tail 50 consolidation-worker                                                                   # [EXECUTADO]
# 3. banco do consolidado acessível pelo papel do worker?                                                [EXECUTADO em F6 como prova de privilégio]
PGPASSWORD="$(envval CONS_WORKER_PASSWORD)" docker exec -i -e PGPASSWORD cashflow-postgres-1 psql -h 127.0.0.1 -U cons_worker -d consolidation_db -At -c 'SELECT 1'
```
Interpretação: **0 consumidores + worker `healthy`/`live`** = caso de falha silenciosa que C-19/C-21 corrigiu; tratar como incidente (reiniciar o worker, passo 2).

## Ação
1. Banco do consolidado ou broker fora: restaurar; o worker retoma sozinho (reavaliação a cada 5 s). Ver `broker-indisponivel.md`.
2. Worker vivo sem consumidor por mais de 1-2 min após dependências saudáveis: `$C restart consolidation-worker` (seguro: ACK por mensagem, Inbox absorve reentrega). **[restart NÃO EXECUTADO em F6]**
3. Confirmar: `list_consumers` = 1, `ready` = 200, fila drenando (`messages` → 0), `dailyNet` convergindo.
4. Lag residual com a fila vazia (evento publicado e perdido): `reconcile` do intervalo (ver `reconciliacao-e-rebuild.md`).
5. Mensagens que viraram DLQ durante o incidente: ver `dlq.md` (principalmente o caso **sem `x-reason`**).

## Execução real (F6)
`docs/testing/evidence/f6/11-dlq-scenarios.log`: parar o worker (`docker stop`) deixa 0 consumidores; `docker start` o reinicia. Parada de 70 s do broker: `12-runbook-broker-down.log`
(`ready` do worker = 503 após 70 s sem consumidor, 200 cerca de 17 s de relógio depois do `start` (08:15:40 → 08:15:57), ~12 s pelo contador do roteiro; linhas 7-8 do log). Nota: o log 12 mostra uma única sonda de `ready` do tx-api (200) e uma do worker aos 70 s; não é uma série contínua.
