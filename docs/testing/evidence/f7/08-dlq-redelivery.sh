#!/usr/bin/env bash
# F7 (T-04/T-05/T-10, itens herdados de F6): redelivery, DLQ, redrive e purge contra a stack Compose BASE REAL.
#  D1 mensagem nao-JSON -> DLQ (x-reason) e contagem IMEDIATA por rabbitmqctl (sem o atraso das estatisticas da API de gestao)
#  D2 redelivery: o MESMO evento (message_id = eventId da outbox) republicado 5x -> projecao e Inbox inalterados
#  D3 redrive ponta a ponta: evento valido e NAO aplicado vai para a DLQ (worker parado, reject sem requeue) -> recon dlq redrive
#     -> aplicado exatamente uma vez; reconcile da data sem divergencia
#  D4 redrive de mensagem invalida (comportamento observado, nao assumido)
#  D5 purge da DLQ pelo recon-tool (PF-09): por id e total, uma trilha em reconciliation_runs por mensagem descartada
# Usa a data 2026-03-10 (sem dados previos). Credenciais do broker nunca impressas. Destrutivo so para mensagens DE TESTE.
# Uso: bash 08-dlq-redelivery.sh > 08-dlq-redelivery.log 2>&1 ; echo "exit=$?" >> 08-dlq-redelivery.log
#      ONLY=D2 bash 08-dlq-redelivery.sh > 08-d2-redelivery-reexec.log 2>&1 ; echo "exit=$?" >> 08-d2-redelivery-reexec.log   (so o D2)
# HISTORICO: o D2 desta versao foi reescrito depois da revisao (publish agora e verificado: routed:true 5x e 5 linhas de Reentrega no worker).
#            O 08-dlq-redelivery.log canonico foi gerado ANTES; seu D2 nao tinha prova de consumo. Ver 08-d2-redelivery-reexec.log.
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
RECON="$C --profile tools run --rm recon-tool"
FAIL=0
RUN=$(date +%s)  # chave de idempotencia unica por execucao (mesma chave + payload diferente = 422, ADR-0005)
expect() { if [ "$2" = "$3" ]; then echo "  OK: $1 = $3"; else echo "  FALHA: $1 esperado='$2' obtido='$3'"; FAIL=$((FAIL+1)); fi; }
qn() { docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name messages_ready messages_unacknowledged 2>/dev/null | tr '\t' ' ' | awk -v q="$1" '$1==q{print $2+$3}'; }
qmgmt() { mq "$MQ/queues/%2F/$1" | python -I -c 'import json,sys; print(json.load(sys.stdin).get("messages"))'; }
publish() { # publish <exchange> <routing-key> <message_id> <extra headers json> <payload>  (mq usa stdin para as credenciais: corpo vai inline em -d)
  local body; body=$(python -I -c '
import json, sys
ex, rk, mid, hdr, payload = sys.argv[1:6]
print(json.dumps({"properties": {"delivery_mode": 2, "type": "TransactionRegistered", "message_id": mid, "content_type": "application/json", "headers": json.loads(hdr)}, "routing_key": rk, "payload": payload, "payload_encoding": "string"}))' "$@")
  mq -X POST "$MQ/exchanges/%2F/$1/publish" -d "$body"; echo; }
peek() { mq -X POST "$MQ/queues/%2F/consolidation.dlq/get" -d '{"count":20,"ackmode":"ack_requeue_true","encoding":"auto","truncate":120}' | python -I -c '
import json,sys
for m in json.load(sys.stdin):
    p=m["properties"]; h=p.get("headers",{})
    print("  DLQ: message_id=%s x-reason=%s x-reason-detail=%s x-original-routing-key=%s x-death=%s" % (p.get("message_id"), h.get("x-reason"), h.get("x-reason-detail"), h.get("x-original-routing-key"), "sim" if "x-death" in h else "nao"))'; }

echo "# F7 08 - redelivery / DLQ / redrive / purge - $(date -u +%FT%TZ)"
mktokens; D8=${D8:-2026-03-10}   # D1-D5 absolutos (D3) exigem data SEM dados previos: apos a 1a execucao use D8=<outra data>
echo "estado inicial: DLQ(rabbitmqctl)=$(qn consolidation.dlq) fila principal=$(qn consolidation.transaction-registered) consumidores: $(docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name consumers 2>/dev/null | tr '\t' ' ' | grep transaction-registered)"
DLQ0=$(qn consolidation.dlq)

if [ "${ONLY:-}" != D2 ]; then
sec "D1. mensagem nao-JSON -> DLQ"
publish cashflow.transactions transaction.registered.v1 f7-d1-malformed '{}' 'nao-e-json'
for i in $(seq 1 20); do [ "$(qn consolidation.dlq)" -ge $((DLQ0+1)) ] && break; sleep 0.5; done
expect "DLQ aumentou em 1 (rabbitmqctl imediato)" $((DLQ0+1)) "$(qn consolidation.dlq)"
peek
fi

if [ -z "${ONLY:-}" ] || [ "$ONLY" = D2 ]; then
sec "D2. redelivery do MESMO evento (5x) -> sem dupla contabilizacao (COM prova de consumo: routed:true 5x e 5 linhas 'Reentrega ... sem efeito' no worker)"
# Correcao pos-revisao: na versao anterior o resultado do publish era descartado e nenhuma assercao provava que as 5 mensagens
# foram roteadas e consumidas; as assercoes de estado final passariam mesmo se nada tivesse sido publicado.
DLQ_D2=$(qn consolidation.dlq)
P2=$(post "$A" "$TAW" f7-d2-$RUN '{"type":"CREDIT","amount":"12.34","effectiveDate":"'$D8'","description":"f7-d2"}'); echo "POST A 12.34 em $D8 -> $P2"; [ "$P2" = 201 ] || { echo "ABORTADO: POST inicial nao foi 201 (evita publicar mensagens sem eventId); FALHAS=1"; exit 1; }
converge "$A" "$TA" "$D8" 30
EID=$($PSQL_T -c "SELECT payload->>'eventId' FROM outbox_messages WHERE merchant_id='$A' AND payload->>'effectiveDate'='$D8' ORDER BY occurred_at DESC LIMIT 1")
PAYLOAD=$($PSQL_T -c "SELECT payload::text FROM outbox_messages WHERE payload->>'eventId'='$EID'")
TID=$($PSQL_T -c "SELECT payload->>'transactionId' FROM outbox_messages WHERE payload->>'eventId'='$EID'")
echo "eventId=$EID transactionId=$TID; linhas de outbox com este eventId: $($PSQL_T -c "SELECT count(*) FROM outbox_messages WHERE payload->>'eventId'='$EID'")"
expect "a transacao deste POST e a que acabou de ser aplicada (credito 12.34 na fonte para a chave f7-d2-$RUN)" "12.34" "$($PSQL_T -c "select amount from transactions where id='$TID'")"
BASE_PROJ=$(proj "$A" "$D8"); BASE_INBOX=$($PSQL_C -c "select count(*) from processed_messages where merchant_id='$A' and effective_date='$D8'")
echo "baseline apos o 1o processamento: projecao='$BASE_PROJ' Inbox=$BASE_INBOX"
ROUTED=0
for i in 1 2 3 4 5; do
  R=$(publish cashflow.transactions transaction.registered.v1 "$EID" "{\"merchant_id\":\"$A\"}" "$PAYLOAD"); echo "  publish #$i -> $R"
  case "$R" in *'"routed":true'*) ROUTED=$((ROUTED+1));; esac
done
expect "publicacoes com routed:true" 5 "$ROUTED"
DUP=0
for i in $(seq 1 30); do DUP=$(docker logs cashflow-consolidation-worker-1 2>&1 | grep -c "Reentrega do lan.*$TID.*sem efeito"); [ "$DUP" -ge 5 ] && break; sleep 1; done
echo "linhas 'Reentrega' do worker para a transactionId $TID (UUID novo: so podem ser deste teste): $DUP (apos ${i}s)"
expect "linhas 'Reentrega do lancamento $TID ... sem efeito' no log do worker" 5 "$DUP"
sleep 2
expect "projecao (credits debits count) inalterada apos 5 redeliveries" "$BASE_PROJ" "$(proj "$A" "$D8")"
expect "Inbox: linhas da data inalteradas" "$BASE_INBOX" "$($PSQL_C -c "select count(*) from processed_messages where merchant_id='$A' and effective_date='$D8'")"
expect "DLQ nao cresceu (duplicata e ACK, nao DLQ)" "$DLQ_D2" "$(qn consolidation.dlq)"
expect "fila principal vazia" 0 "$(qn consolidation.transaction-registered)"
fi

if [ "${ONLY:-}" != D2 ]; then
sec "D3. redrive ponta a ponta: evento valido nunca aplicado -> DLQ -> recon dlq redrive -> aplicado 1x"
echo "worker parado; POST A 100.00 em $D8"
$C stop consolidation-worker 2>&1 | tail -1
echo "POST -> $(post "$A" "$TAW" f7-d3-$RUN '{"type":"CREDIT","amount":"100.00","effectiveDate":"'$D8'","description":"f7-d3"}')"
for i in $(seq 1 20); do [ "$(qn consolidation.transaction-registered)" = 1 ] && break; sleep 0.5; done
echo "fila principal=$(qn consolidation.transaction-registered) (esperado 1: publicada pela outbox, sem consumidor)"
echo "rejeito a mensagem SEM requeue (vai para a DLX/DLQ, como se o worker a tivesse recusado)"
mq -X POST "$MQ/queues/%2F/consolidation.transaction-registered/get" -d '{"count":1,"ackmode":"reject_requeue_false","encoding":"auto","truncate":50}' | python -I -c 'import json,sys; print("  mensagem rejeitada:", [m["properties"].get("message_id") for m in json.load(sys.stdin)])'
sleep 1; echo "DLQ=$(qn consolidation.dlq) (esperado $((DLQ0+2)))"; peek
$C start consolidation-worker 2>&1 | tail -1; wait_healthy cashflow-consolidation-worker-1 120
sleep 3
echo "antes do redrive: fonte=$(src "$A" "$D8") projecao=$(proj "$A" "$D8") (esperado projecao SEM os 100.00)"
$RECON dlq redrive --all 2>&1 | tail -15; echo "redrive exit=${PIPESTATUS[0]}"
mktokens; converge "$A" "$TA" "$D8" 60
expect "projecao apos o redrive (credits debits count)" "112.34 0.00 2" "$(proj "$A" "$D8")"
expect "Inbox da data (1 por lancamento)" 2 "$($PSQL_C -c "select count(*) from processed_messages where merchant_id='$A' and effective_date='$D8'")"
echo "--- DLQ apos o redrive (a mensagem malformada do D1 e republicada e deve voltar a DLQ ou ser recusada; ver D4)"
sleep 2; echo "DLQ=$(qn consolidation.dlq)"; peek
echo "--- reconcile da data (esperado 0 divergencias)"
$RECON reconcile --merchant "$A" --from "$D8" --to "$D8" 2>&1 | tail -8; echo "reconcile exit=${PIPESTATUS[0]}"

sec "D4. redrive de mensagens invalidas: comportamento observado"
echo "DLQ antes=$(qn consolidation.dlq)"
$RECON dlq redrive --all 2>&1 | tail -8; echo "redrive exit=${PIPESTATUS[0]}"
sleep 3; echo "DLQ depois=$(qn consolidation.dlq)"; peek

sec "D5. purge da DLQ com trilha (recon-tool dlq purge; o DELETE /contents da API de gestao nao e mais usado: nao deixava registro)"
publish cashflow.transactions transaction.registered.v1 f7-d5-a-$RUN '{}' 'lixo-a'
publish cashflow.transactions transaction.registered.v1 f7-d5-b-$RUN '{}' 'lixo-b'
sleep 3; N0=$(qn consolidation.dlq)
TR0=$($PSQL_C -c "SELECT count(*) FROM reconciliation_runs WHERE details->>'mode'='dlq_purge'")
echo "DLQ antes do purge (rabbitmqctl)=$N0 | trilhas dlq_purge antes=$TR0"
echo "--- purge de UMA mensagem por id (as demais ficam)"
$RECON dlq purge --id f7-d5-a-$RUN --reason "f7-08 D5: mensagem de teste nao-JSON" 2>&1 | tail -3; echo "purge --id exit=${PIPESTATUS[0]}"
expect "DLQ apos purge por id (rabbitmqctl)" $((N0-1)) "$(qn consolidation.dlq)"
echo "--- purge do restante"
$RECON dlq purge --all --reason "f7-08 D5: limpeza das mensagens de teste" 2>&1 | tail -3; echo "purge --all exit=${PIPESTATUS[0]}"
expect "DLQ vazia apos o purge (rabbitmqctl)" 0 "$(qn consolidation.dlq)"
TR1=$($PSQL_C -c "SELECT count(*) FROM reconciliation_runs WHERE details->>'mode'='dlq_purge'")
expect "uma trilha dlq_purge por mensagem descartada" "$N0" "$((TR1-TR0))"
echo "--- trilhas gravadas nesta execucao (sem corpo da mensagem; so o SHA-256)"
$PSQL_C -c "SELECT details->>'messageId', details->>'xReason', left(details->>'bodySha256',16), details->>'operatorReason' FROM reconciliation_runs WHERE details->>'mode'='dlq_purge' ORDER BY started_at DESC LIMIT $N0" | sed 's/^/  /'
fi

sec "estado final"
qctl
$C ps --format 'table {{.Name}}\t{{.Status}}'
echo "FALHAS=$FAIL"
[ "$FAIL" = 0 ]
