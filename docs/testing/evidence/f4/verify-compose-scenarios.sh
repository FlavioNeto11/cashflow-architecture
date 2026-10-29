#!/usr/bin/env bash
# F4 (verificacao): stack do zero + cenarios de falha target.md 10.3-10.7 contra o Compose versionado (base + dev.yml).
# Oraculos: ADR-0002 (100,00 nao 200,00; 69,80), inbox dedupe, DLQ com x-reason, pausa sem DLQ, RNF-01 readiness.
# AJUSTAR apos o retorno da implementacao: RECON (invocacao), header de escopo da consolidation-api, nomes de fila/exchange.
set -u
cd "$(dirname "$0")/../../../.." || exit 1
S="${TMPDIR:-/tmp}/cashflow-f4"; mkdir -p "$S"
C="docker compose -f infra/compose/docker-compose.yml -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env"
H="$S/h4.txt"; B="$S/b4.json"
TX=http://127.0.0.1:18080/api/v1
CONS=http://127.0.0.1:18082/api/v1
A=33333333-3333-4333-8333-333333333333
BM=44444444-4444-4444-8444-444444444444
TODAY=$(date -u +%F)
HA=(-H "X-Dev-Sub: user-a" -H "X-Dev-Client-Id: cashflow-web" -H "X-Dev-Merchant-Id: $A" -H "X-Dev-Scope: transactions.write transactions.read consolidation.read")
HB=(-H "X-Dev-Sub: user-b" -H "X-Dev-Client-Id: cashflow-web" -H "X-Dev-Merchant-Id: $BM" -H "X-Dev-Scope: transactions.write transactions.read consolidation.read")
HNOSCOPE=(-H "X-Dev-Sub: user-a" -H "X-Dev-Client-Id: cashflow-web" -H "X-Dev-Merchant-Id: $A" -H "X-Dev-Scope: transactions.read")
J=(-H "Content-Type: application/json")
# credenciais do broker so para a API de gestao local (nunca impressas)
set -a; . infra/compose/.env; set +a
MQ="http://127.0.0.1:15672/api"
# F6 (C-20): a API de gestao exige o usuario de OPERACAO (RABBITMQ_OPS_*); o da aplicacao nao tem tag e recebe 401.
mq() { curl -sS -u "${RABBITMQ_OPS_USER:?RABBITMQ_OPS_USER ausente em infra/compose/.env}:${RABBITMQ_OPS_PASS:?}" "$@"; }
PSQL_C="docker exec -i cashflow-postgres-1 psql -U migrator -d consolidation_db -v ON_ERROR_STOP=1 -At"
PSQL_T="docker exec -i cashflow-postgres-1 psql -U migrator -d transactions_db -v ON_ERROR_STOP=1 -At"
RECON="$C --profile tools run --rm recon-tool"

ts() { date -u +%H:%M:%S; }
lc() { docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name 2>/dev/null | grep -c consolidation.transaction-registered; }
wready() { curl -s -o /dev/null -w '%{http_code}' --max-time 3 http://127.0.0.1:18084/health/ready; }
whealth() { docker inspect --format '{{.State.Health.Status}}' cashflow-consolidation-worker-1 2>/dev/null; }
wait_lc() { local exp="$1" t="${2:-40}" got=""; for i in $(seq 1 "$t"); do got=$(lc); [ "$got" = "$exp" ] && break; sleep 1; done; echo "[$(ts)] list_consumers=$got (esperado $exp) apos ${i}s"; }
req() { local label="$1"; shift; echo "--- [$(ts)] $label"; curl -sS -D "$H" -o "$B" "$@"; echo "status: $(head -1 "$H" | tr -d '\r')"; echo "  body: $(head -c 600 "$B")"; echo; }
post() { # post <label> <key> <json> [headers...]
  local label="$1" key="$2" json="$3"; shift 3; local hdr=("${HA[@]}"); [ $# -gt 0 ] && hdr=("$@")
  req "$label" "${hdr[@]}" "${J[@]}" -X POST "$TX/merchants/$A/transactions" -H "Idempotency-Key: $key" -d "$json"; }
daily() { curl -sS "${HA[@]}" "$CONS/merchants/$A/consolidations/daily?date=$1${2:-}"; }
field() { sed -n "s/.*\"$1\": *\"\{0,1\}\([^\",}]*\)\"\{0,1\}.*/\1/p" | head -1; }
wait_net() { # wait_net <date> <expected dailyNet> [timeout s]
  local d="$1" exp="$2" t="${3:-60}" got=""; for i in $(seq 1 "$t"); do got=$(daily "$d" | field dailyNet); [ "$got" = "$exp" ] && break; sleep 1; done
  echo "[$(ts)] dailyNet($d) = $got (esperado $exp) apos ${i}s"; daily "$d"; echo; }
queue() { mq "$MQ/queues/%2F/$1" | python -I -c 'import json,sys; q=json.load(sys.stdin); print("fila %s: messages=%s ready=%s unacked=%s consumers=%s" % (q["name"], q.get("messages"), q.get("messages_ready"), q.get("messages_unacknowledged"), q.get("consumers")))'; }
outbox() { $PSQL_T -c "SELECT count(*) FILTER (WHERE published_at IS NULL) AS pendentes, count(*) AS total, coalesce(max(attempts),0) AS max_attempts, max(last_error) AS last_error FROM outbox_messages;"; }
publish_json() { # publish_json <exchange> <routing-key> <properties-json> <payload-json>
  python -I - "$1" "$2" "$3" "$4" <<'PY' | mq -H 'content-type: application/json' -X POST "$MQ/exchanges/%2F/$1/publish" -d @-
import json, sys
ex, rk, props, payload = sys.argv[1:5]
print(json.dumps({"properties": json.loads(props), "routing_key": rk, "payload": payload, "payload_encoding": "string"}))
PY
echo; }

echo "# F4 — stack do zero e cenarios de falha (verificacao) — $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "## 0. stack do zero"
$C down -v --remove-orphans 2>&1 | tail -3
$C up -d --build 2>&1 | tail -15; echo "up exit=${PIPESTATUS[0]}"
$C --profile tools build recon-tool 2>&1 | tail -2; echo "build recon-tool exit=${PIPESTATUS[0]}"
for i in $(seq 1 90); do n=$($C ps --format '{{.Name}} {{.Health}}' | grep -c healthy); [ "$n" -ge 6 ] && break; sleep 2; done
$C ps --format 'table {{.Name}}\t{{.Status}}'
echo "--- topologia declarada pela infraestrutura"
mq "$MQ/exchanges/%2F/cashflow.transactions" | head -c 300; echo
mq "$MQ/queues/%2F/consolidation.transaction-registered" | python -I -c 'import json,sys; q=json.load(sys.stdin); print("fila principal:", q.get("type"), q.get("arguments"))'
queue consolidation.transaction-registered; queue consolidation.dlq
echo "--- readiness"; for p in 18081 18083; do echo "$p: $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:$p/health/ready)"; done; echo "18084 live: $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18084/health/live)"
echo

echo "## 1. caminho feliz (RF-09): 100,00 + 35,20 -> 64,80 em $TODAY; retroativo 5,00 em 2026-10-01"
post "credito 100.00 (k1)" k1 '{"type":"CREDIT","amount":"100.00","currency":"BRL","description":"venda"}'
post "debito 35.20 (k2)" k2 '{"type":"DEBIT","amount":"35.20"}'
post "retroativo 5.00 em 2026-10-01 (k3)" k3 '{"type":"CREDIT","amount":"5.00","effectiveDate":"2026-10-01"}'
wait_net "$TODAY" 64.80; wait_net 2026-10-01 5.00
req "dia sem movimento -> zeros, asOf null" "${HA[@]}" "$CONS/merchants/$A/consolidations/daily?date=2026-09-01"
req "closingBalance em 2026-10-01 -> 5.00" "${HA[@]}" "$CONS/merchants/$A/consolidations/daily?date=2026-10-01&include=closingBalance"
req "closingBalance em $TODAY -> 69.80" "${HA[@]}" "$CONS/merchants/$A/consolidations/daily?date=$TODAY&include=closingBalance"
req "sem principal -> 401" "$CONS/merchants/$A/consolidations/daily?date=$TODAY"
req "sem escopo consolidation.read -> 403" "${HNOSCOPE[@]}" "$CONS/merchants/$A/consolidations/daily?date=$TODAY"
req "claim B na rota de A -> 404" "${HB[@]}" "$CONS/merchants/$A/consolidations/daily?date=$TODAY"
req "date invalida -> 400/422" "${HA[@]}" "$CONS/merchants/$A/consolidations/daily?date=2026-13-40"
outbox; queue consolidation.transaction-registered; queue consolidation.dlq
echo

echo "## 2. §10.3 consolidado parado (worker + api): POST continua 201; apos start, backlog aplicado"
$C stop consolidation-worker consolidation-api 2>&1 | tail -2
post "credito 10.00 com consolidado parado (k4) -> 201" k4 '{"type":"CREDIT","amount":"10.00"}'
echo "GET consolidado com api parada: $(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "${HA[@]}" "$CONS/merchants/$A/consolidations/daily?date=$TODAY" || echo 'conexao recusada (sem gateway ate F5)')"
queue consolidation.transaction-registered
$C start consolidation-worker consolidation-api 2>&1 | tail -2
wait_net "$TODAY" 74.80 90
echo

echo "## 3. §10.4 broker parado (RNF-01): POST 201; readiness da transactions-api saudavel; outbox acumula; broker volta -> publicado"
$C stop rabbitmq 2>&1 | tail -1
post "credito 20.00 com broker parado (k5) -> 201" k5 '{"type":"CREDIT","amount":"20.00"}'
for i in 1 2 3; do echo "readiness tx-api com broker parado: $(curl -s -o /dev/null -w '%{http_code} %{time_total}s' http://127.0.0.1:18081/health/ready) | worker ready: $(wready) live: $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18084/health/live) | list_consumers: $(lc)"; sleep 4; done
outbox
$C start rabbitmq 2>&1 | tail -1
for i in $(seq 1 60); do p=$($PSQL_T -c "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL"); [ "$p" = "0" ] && break; sleep 1; done; echo "[$(ts)] outbox pendentes=$p apos ${i}s"; outbox
wait_net "$TODAY" 94.80 90
wait_lc 1 40; echo "worker ready apos broker voltar: $(wready) | compose health: $(whealth)"
echo

echo "## 4. §10.5 / T-03 reentrega e replay: mesma mensagem publicada de novo (API de gestao) -> ROLLBACK + ACK, projecao inalterada"
EID=$($PSQL_T -c "SELECT payload->>'eventId' FROM outbox_messages WHERE merchant_id='$A' ORDER BY occurred_at LIMIT 1")
PAYLOAD=$($PSQL_T -c "SELECT payload::text FROM outbox_messages WHERE merchant_id='$A' ORDER BY occurred_at LIMIT 1")
echo "evento duplicado: message-id=$EID"
publish_json cashflow.transactions transaction.registered.v1 "{\"delivery_mode\":2,\"message_id\":\"$EID\",\"type\":\"TransactionRegistered\",\"content_type\":\"application/json\",\"headers\":{\"merchant_id\":\"$A\"}}" "$PAYLOAD"
sleep 3; wait_net "$TODAY" 94.80 5
echo "inbox: $($PSQL_C -c "SELECT count(*) FROM processed_messages WHERE merchant_id='$A'") linhas (esperado = lancamentos de A: $($PSQL_T -c "SELECT count(*) FROM transactions WHERE merchant_id='$A'"))"
echo "--- docker kill do worker durante rajada de 10 POST (crash sem ACK); reinicio; soma deve bater com a fonte"
$C stop consolidation-worker 2>&1 | tail -1
for i in $(seq 1 10); do curl -s -o /dev/null -w "%{http_code} " "${HA[@]}" "${J[@]}" -X POST "$TX/merchants/$A/transactions" -H "Idempotency-Key: burst-$i" -d '{"type":"CREDIT","amount":"1.00"}'; done; echo
sleep 3; queue consolidation.transaction-registered
$C start consolidation-worker 2>&1 | tail -1; sleep 0.4; docker kill -s KILL cashflow-consolidation-worker-1 >/dev/null 2>&1; echo "[$(ts)] worker morto (SIGKILL) 0,4 s apos start"
sleep 2; queue consolidation.transaction-registered
$C start consolidation-worker 2>&1 | tail -1
wait_net "$TODAY" 104.80 90
echo

echo "## 5. §10.6 payload invalido -> DLQ com x-reason; projecao inalterada; infra fora NUNCA manda para a DLQ"
publish_json cashflow.transactions transaction.registered.v1 "{\"delivery_mode\":2,\"message_id\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"TransactionRegistered\",\"content_type\":\"application/json\",\"headers\":{\"merchant_id\":\"$A\"}}" "{\"eventId\":\"11111111-1111-4111-8111-111111111111\",\"type\":\"TransactionRegistered\",\"version\":1,\"occurredAt\":\"${TODAY}T10:00:00Z\",\"merchantId\":\"$A\",\"transactionId\":\"22222222-2222-4222-8222-222222222222\",\"entryType\":\"CREDIT\",\"amount\":\"1.00\",\"currency\":\"USD\",\"effectiveDate\":\"$TODAY\",\"reversalOf\":null}"
publish_json cashflow.transactions transaction.registered.v1 "{\"delivery_mode\":2,\"message_id\":\"11111111-1111-4111-8111-111111111112\",\"type\":\"TransactionRegistered\",\"content_type\":\"application/json\",\"headers\":{\"merchant_id\":\"$BM\"}}" "{\"eventId\":\"11111111-1111-4111-8111-111111111112\",\"type\":\"TransactionRegistered\",\"version\":1,\"occurredAt\":\"${TODAY}T10:00:00Z\",\"merchantId\":\"$A\",\"transactionId\":\"22222222-2222-4222-8222-222222222223\",\"entryType\":\"CREDIT\",\"amount\":\"1.00\",\"currency\":\"BRL\",\"effectiveDate\":\"$TODAY\",\"reversalOf\":null}"
publish_json cashflow.transactions transaction.registered.v1 "{\"delivery_mode\":2,\"message_id\":\"11111111-1111-4111-8111-111111111113\",\"content_type\":\"application/json\"}" "nao-e-json"
sleep 3; queue consolidation.dlq
mq -X POST "$MQ/queues/%2F/consolidation.dlq/get" -H 'content-type: application/json' -d '{"count":5,"ackmode":"ack_requeue_true","encoding":"auto"}' | python -I -c 'import json,sys
for m in json.load(sys.stdin):
    p=m["properties"]; h=p.get("headers",{})
    print("DLQ:", "message_id=",p.get("message_id"), "x-reason=",h.get("x-reason"), "x-original-routing-key=",h.get("x-original-routing-key"), "x-death=", "sim" if "x-death" in h else "nao", "payload=", m["payload"][:80])'
wait_net "$TODAY" 104.80 3
echo "--- pausa: consolidation_db sem conexoes (ALLOW_CONNECTIONS false + terminate cons_worker) durante 2 POST -> sem ACK, consumers=0, DLQ inalterada; volta -> aplicados uma vez"
DLQ_BEFORE=$(mq "$MQ/queues/%2F/consolidation.dlq" | python -I -c 'import json,sys; print(json.load(sys.stdin).get("messages"))')
docker exec -i cashflow-postgres-1 psql -U postgres -d postgres -v ON_ERROR_STOP=1 -At -c "ALTER DATABASE consolidation_db ALLOW_CONNECTIONS false;" -c "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname='consolidation_db' AND usename IN ('cons_worker','cons_ro');"
post "credito 2.00 com consolidation_db fora (k6) -> 201" k6 '{"type":"CREDIT","amount":"2.00"}'
post "credito 3.00 com consolidation_db fora (k7) -> 201" k7 '{"type":"CREDIT","amount":"3.00"}'
for i in $(seq 1 14); do sleep 5; echo "[$(ts)] $(queue consolidation.transaction-registered) | list_consumers: $(lc) | worker live: $(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18084/health/live) ready: $(wready) compose: $(whealth)"; done
echo "[$(ts)] esperado ao fim da pausa (>60 s sem consumidor): ready=503, compose=unhealthy (C-21); DLQ inalterada"
echo "DLQ antes=$DLQ_BEFORE depois=$(mq "$MQ/queues/%2F/consolidation.dlq" | python -I -c 'import json,sys; print(json.load(sys.stdin).get("messages"))') (esperado igual)"
docker exec -i cashflow-postgres-1 psql -U postgres -d postgres -At -c "ALTER DATABASE consolidation_db ALLOW_CONNECTIONS true;"
wait_net "$TODAY" 109.80 120
echo "--- retomada (P0 da verificacao, C-19): consumidor re-registrado em list_consumers; 3o lancamento consumido; ready volta a 200"
wait_lc 1 60; echo "[$(ts)] $(queue consolidation.transaction-registered) | worker ready: $(wready) compose: $(whealth)"
post "credito 1.00 apos a retomada (k-resume) -> 201" k-resume '{"type":"CREDIT","amount":"1.00"}'
wait_net "$TODAY" 110.80 60
echo "[$(ts)] $(queue consolidation.transaction-registered) | list_consumers: $(lc) | worker ready: $(wready) compose: $(whealth)"
echo "--- worker: linhas de pausa/retomada"; docker logs --since 3m cashflow-consolidation-worker-1 2>&1 | grep -o '"Message":"[^"]*\(pausado\|retomado\|novamente\|indispon\)[^"]*"' | sort | uniq -c
echo

echo "## 6. §10.7 / T-14a reconciliacao x evento em transito (opcao b)"
echo "--- (i) broker parado -> POST 100,00 em 2026-10-02 -> recon -> broker volta -> 100,00 (nao 200,00)"
$C stop rabbitmq 2>&1 | tail -1
post "credito 100.00 em 2026-10-02 com broker parado (k8) -> 201" k8 '{"type":"CREDIT","amount":"100.00","effectiveDate":"2026-10-02"}'
$RECON reconcile --merchant "$A" --from 2026-10-02 --to 2026-10-02 2>&1 | tail -15; echo "recon exit=$?"
wait_net 2026-10-02 100.00 3
$C start rabbitmq 2>&1 | tail -1
for i in $(seq 1 60); do p=$($PSQL_T -c "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL"); [ "$p" = "0" ] && break; sleep 1; done; echo "[$(ts)] outbox pendentes=$p apos ${i}s"
sleep 3; wait_net 2026-10-02 100.00 5
echo "inbox 2026-10-02: $($PSQL_C -c "SELECT consumer || ':' || count(*) FROM processed_messages WHERE merchant_id='$A' AND effective_date='2026-10-02' GROUP BY consumer")"
echo "--- (ii) linha 64,80 em 2026-10-03; broker parado; retroativo 5,00; recon -> 69,80; broker volta -> 69,80"
post "credito 100.00 em 2026-10-03 (k9)" k9 '{"type":"CREDIT","amount":"100.00","effectiveDate":"2026-10-03"}'
post "debito 35.20 em 2026-10-03 (k10)" k10 '{"type":"DEBIT","amount":"35.20","effectiveDate":"2026-10-03"}'
wait_net 2026-10-03 64.80
$C stop rabbitmq 2>&1 | tail -1
post "retroativo 5.00 em 2026-10-03 com broker parado (k11) -> 201" k11 '{"type":"CREDIT","amount":"5.00","effectiveDate":"2026-10-03"}'
$RECON reconcile --merchant "$A" --from 2026-10-03 --to 2026-10-03 2>&1 | tail -15; echo "recon exit=$?"
wait_net 2026-10-03 69.80 3
$C start rabbitmq 2>&1 | tail -1
for i in $(seq 1 60); do p=$($PSQL_T -c "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL"); [ "$p" = "0" ] && break; sleep 1; done; echo "[$(ts)] outbox pendentes=$p apos ${i}s"
sleep 3; wait_net 2026-10-03 69.80 5
echo "--- segunda execucao: 0 divergencias"
$RECON reconcile --merchant "$A" --from 2026-10-01 --to "$TODAY" 2>&1 | tail -15; echo "recon exit=$?"
echo "--- corrupcao induzida (credits+1 em 2026-10-03 como migrator) -> corrected_corruption -> 69,80"
$PSQL_C -c "UPDATE daily_consolidations SET credits = credits + 1 WHERE merchant_id='$A' AND effective_date='2026-10-03'"
wait_net 2026-10-03 70.80 3
$RECON reconcile --merchant "$A" --from 2026-10-03 --to 2026-10-03 2>&1 | tail -15; echo "recon exit=$?"
wait_net 2026-10-03 69.80 3
echo "--- rebuild de 2026-10-03 -> 69,80 e inbox reinserido"
$RECON rebuild --merchant "$A" --from 2026-10-03 --to 2026-10-03 2>&1 | tail -10; echo "rebuild exit=$?"
wait_net 2026-10-03 69.80 3
echo "--- dlq redrive --all: mensagem VALIDA ja aplicada colocada na DLX a mao + as 3 invalidas -> valida absorvida pelo Inbox; invalidas republicadas voltam a DLQ (politica da implementacao) ou sao recusadas (sem occurredAt legivel)"
publish_json cashflow.dlx transaction.registered.v1 "{\"delivery_mode\":2,\"message_id\":\"$EID\",\"type\":\"TransactionRegistered\",\"content_type\":\"application/json\",\"headers\":{\"merchant_id\":\"$A\",\"x-reason\":\"manual-test\",\"x-original-routing-key\":\"transaction.registered.v1\"}}" "$PAYLOAD"
sleep 1; queue consolidation.dlq
$RECON dlq redrive --all 2>&1 | tail -15; echo "redrive exit=$?"
sleep 3; queue consolidation.dlq; wait_net "$TODAY" 110.80 3
echo "reconciliation_runs por kind:"; $PSQL_C -c "SELECT kinds::text, count(*) FROM reconciliation_runs GROUP BY 1 ORDER BY 2 DESC"
echo

echo "## 7. cross-check fonte x projecao"
echo "fonte (transactions, A) por data: "; $PSQL_T -c "SELECT effective_date, sum(CASE WHEN entry_type=1 THEN amount ELSE 0 END), sum(CASE WHEN entry_type=2 THEN amount ELSE 0 END), count(*) FROM transactions WHERE merchant_id='$A' GROUP BY 1 ORDER BY 1"
echo "projecao (daily_consolidations, A): "; $PSQL_C -c "SELECT effective_date, credits, debits, entries_count FROM daily_consolidations WHERE merchant_id='$A' ORDER BY 1"
echo "inbox por consumer: "; $PSQL_C -c "SELECT consumer, count(*) FROM processed_messages GROUP BY 1"
echo "lancamentos A: $($PSQL_T -c "SELECT count(*) FROM transactions WHERE merchant_id='$A'") | inbox A: $($PSQL_C -c "SELECT count(*) FROM processed_messages WHERE merchant_id='$A'")"
outbox; queue consolidation.transaction-registered; queue consolidation.dlq
echo "## fim — $(date -u +%Y-%m-%dT%H:%M:%SZ)"
