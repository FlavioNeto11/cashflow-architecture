#!/usr/bin/env bash
# F6: prova de que o usuario da aplicacao (sem tag, permissoes minimas) basta para tx-api -> broker -> worker e para o recon-tool,
# e que a UI/API de gestao so aceita o usuario de operacao. Credenciais vem de infra/compose/.env e NUNCA sao impressas.
# Requer a stack base+dev no ar e RABBITMQ_OPS_USER/PASS no broker (override descartavel ou edicao pendente do Compose base).
set -u
cd "$(dirname "$0")/../../../.." || exit 1
OVR="${OVR:-}"   # arquivo de override extra (ex.: o que entrega RABBITMQ_OPS_* ao broker)
C="docker compose -f infra/compose/docker-compose.yml -f infra/compose/docker-compose.dev.yml ${OVR:+-f $OVR} --env-file infra/compose/.env"
set -a; . infra/compose/.env; set +a
MQ=http://127.0.0.1:15672/api
M=55555555-5555-4555-8555-555555555555
D=$(date -u +%F)
HA=(-H "X-Dev-Sub: user-f6" -H "X-Dev-Client-Id: cashflow-web" -H "X-Dev-Merchant-Id: $M" -H "X-Dev-Scope: transactions.write transactions.read consolidation.read")
daily() { curl -sS "${HA[@]}" "http://127.0.0.1:18082/api/v1/merchants/$M/consolidations/daily?date=$D"; }
echo "## 1. gestao: usuario da aplicacao (esperado 401) x usuario de operacao (esperado 200)"
echo "app -> HTTP $(curl -s -o /dev/null -w '%{http_code}' -u "$RABBITMQ_DEFAULT_USER:$RABBITMQ_DEFAULT_PASS" $MQ/overview)"
echo "ops -> HTTP $(curl -s -o /dev/null -w '%{http_code}' -u "$RABBITMQ_OPS_USER:$RABBITMQ_OPS_PASS" $MQ/overview)"
echo "## 2. dailyNet antes"; daily; echo
n0=$(daily | grep -o "\"entriesCount\":[0-9]*" | cut -d: -f2)
echo "## 3. POST de credito 12.34 e debito 2.00 (usuario novo no publisher e no worker)"
for p in 'f6a|{"type":"CREDIT","amount":"12.34"}' 'f6b|{"type":"DEBIT","amount":"2.00"}'; do
  k="f6-$(date +%s)-${p%%|*}"
  curl -sS -o /dev/null -w "POST ${p%%|*} -> %{http_code}\n" "${HA[@]}" -H 'Content-Type: application/json' -X POST "http://127.0.0.1:18080/api/v1/merchants/$M/transactions" -H "Idempotency-Key: $k" -d "${p#*|}"
done
for i in $(seq 1 30); do n=$(daily | grep -o "\"entriesCount\":[0-9]*" | cut -d: -f2); [ "${n:-0}" -ge $((n0+2)) ] && break; sleep 1; done
echo "## 4. dailyNet depois (apos ${i}s)"; daily; echo
echo "## 5. fila principal e DLQ"
curl -s -u "$RABBITMQ_OPS_USER:$RABBITMQ_OPS_PASS" $MQ/queues/%2F/consolidation.transaction-registered | python -I -c 'import json,sys; q=json.load(sys.stdin); print("principal: messages=%s consumers=%s"%(q["messages"],q["consumers"]))'
echo "## 6. recon-tool reconcile com o usuario novo (conecta ao broker so no redrive; aqui exercita banco)"
$C --profile tools run --rm recon-tool reconcile --merchant $M --from $D --to $D 2>&1 | grep -v "libgssapi\|Cannot load\|Creating\|Created" | tail -5
echo "## 7. mensagem invalida publicada pelo operador -> worker copia para cashflow.dlx (write em dlx) -> DLQ"
curl -s -u "$RABBITMQ_OPS_USER:$RABBITMQ_OPS_PASS" -H 'content-type: application/json' -X POST $MQ/exchanges/%2F/cashflow.transactions/publish \
  -d '{"properties":{"message_id":"f6-invalid-1","content_type":"application/json"},"routing_key":"transaction.registered.v1","payload":"nao-e-json","payload_encoding":"string"}'; echo
sleep 4
echo "dlq (rabbitmqctl, oraculo): $(docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name messages | grep dlq)"
echo "## 8. redrive com o usuario novo (read em dlq, write em cashflow.transactions)"
$C --profile tools run --rm recon-tool dlq redrive --all 2>&1 | grep -v "libgssapi\|Cannot load\|Creating\|Created" | tail -8
echo "## 9. logs do worker/tx-api: recusas de acesso do broker (esperado: nenhuma)"
for s in transactions-api consolidation-worker; do echo "$s: $($C logs --since 10m $s 2>&1 | grep -ci 'ACCESS_REFUSED\|access_refused')"; done
