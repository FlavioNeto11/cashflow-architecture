#!/usr/bin/env bash
# F6: observa no broker real (a) o cabecalho de uma mensagem enviada a DLQ pelo worker e (b) a de uma mensagem que estoura
# x-delivery-limit (consumo sem ACK com requeue) - insumo do runbook "DLQ". Usa o usuario de operacao (API de gestao).
# NOTA (passada documental pos-revisao, 2026-10-29): este script foi EDITADO depois da execucao cujo log esta nesta pasta; as edicoes NAO foram reexecutadas
# CORRECAO: o cabecalho de detalhe e 'x-reason-detail' (src/CashFlow.Messaging/Topology.cs:25), nao 'x-field'. O 'x-field=None' do log 11 e ARTEFATO deste script
# (lia o cabecalho errado), nao prova que o worker nao grava o detalhe. Reexecucao fica para a 3a passada (com a stack).
# Credenciais do .env nunca sao impressas. Mensagens de teste ficam na DLQ local (limpe com purge_queue, ver runbook).
set -u
cd "$(dirname "$0")/../../../.." || exit 1
MQ=http://127.0.0.1:15672/api
# credenciais lidas so em subshell e entregues ao curl por stdin (-K -), fora do argv
mq() ( set -a; . infra/compose/.env; set +a; printf 'user = "%s:%s"\n' "$RABBITMQ_OPS_USER" "$RABBITMQ_OPS_PASS" | curl -s -K - -H 'content-type: application/json' "$@" )
peek() { # peek <queue> : le sem consumir (ack_requeue_true) e mostra so cabecalhos relevantes
  mq -X POST "$MQ/queues/%2F/$1/get" -d '{"count":10,"ackmode":"ack_requeue_true","encoding":"auto","truncate":200}' | python -I -c '
import json,sys
for m in json.load(sys.stdin):
    p=m["properties"]; h=p.get("headers",{})
    print("message_id=%s x-reason=%s x-reason-detail=%s routing_key=%s" % (p.get("message_id"), h.get("x-reason"), h.get("x-reason-detail"), m["routing_key"]))
    for d in h.get("x-death",[]): print("   x-death: reason=%s queue=%s exchange=%s count=%s" % (d.get("reason"), d.get("queue"), d.get("exchange"), d.get("count")))
'; }
echo "## a. mensagem nao-JSON publicada pelo operador; worker deve enviar a DLQ com x-reason"
mq -X POST "$MQ/exchanges/%2F/cashflow.transactions/publish" -d '{"properties":{"message_id":"f6-malformed-1","content_type":"application/json"},"routing_key":"transaction.registered.v1","payload":"nao-e-json","payload_encoding":"string"}'; echo
sleep 4; peek consolidation.dlq
echo "## b. worker PARADO; mensagem rejeitada com requeue 7 vezes (x-delivery-limit=5) -> DLQ pelo broker, SEM x-reason"
docker stop cashflow-consolidation-worker-1 >/dev/null; echo "worker parado; consumidores: $(docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name | grep -c transaction-registered)"
mq -X POST "$MQ/exchanges/%2F/cashflow.transactions/publish" -d '{"properties":{"message_id":"f6-deliverylimit-1","content_type":"application/json"},"routing_key":"transaction.registered.v1","payload":"{}","payload_encoding":"string"}'; echo
for i in 1 2 3 4 5 6 7; do
  r=$(mq -X POST "$MQ/queues/%2F/consolidation.transaction-registered/get" -d '{"count":1,"ackmode":"reject_requeue_true","encoding":"auto"}' | head -c 60); echo "tentativa $i: ${r:-<fila vazia>}"
done
sleep 2; peek consolidation.dlq
docker start cashflow-consolidation-worker-1 >/dev/null; echo "worker reiniciado"
echo "## filas"; docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name messages consumers
