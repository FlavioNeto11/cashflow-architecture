#!/usr/bin/env bash
# F6: comandos do runbook "broker indisponivel" executados de verdade (stop -> POST -> outbox pendente -> ready do worker -> start -> drenagem).
# NOTA (passada documental pos-revisao, 2026-10-29): este script foi EDITADO depois da execucao cujo log esta nesta pasta; as edicoes NAO foram reexecutadas
# O log 12 foi gerado com psql -U migrator (dono/DDL; CONNECTION LIMIT 2): evitar em producao. Nenhum papel somente leitura cobre outbox_messages; tx_app (DML, sem DDL) e o menor privilegio disponivel.
set -u
cd "$(dirname "$0")/../../../.." || exit 1
set -a; . infra/compose/.env; set +a
M=55555555-5555-4555-8555-555555555555; D=$(date -u +%F)
HA=(-H "X-Dev-Sub: user-f6" -H "X-Dev-Client-Id: cashflow-web" -H "X-Dev-Merchant-Id: $M" -H "X-Dev-Scope: transactions.write transactions.read consolidation.read")
ts() { date -u +%H:%M:%S; }
consumers() { docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name 2>/dev/null | grep -c transaction-registered; }
outbox() { docker exec -i cashflow-postgres-1 psql -U tx_app -d transactions_db -At -c "SELECT 'pendentes='||count(*) FILTER (WHERE published_at IS NULL)||' idade_max_s='||coalesce(round(extract(epoch FROM now()-min(occurred_at) FILTER (WHERE published_at IS NULL))),0) FROM outbox_messages" 2>&1; }
net() { curl -sS "${HA[@]}" "http://127.0.0.1:18082/api/v1/merchants/$M/consolidations/daily?date=$D" | grep -o '"dailyNet":"[^"]*"'; }
ready() { curl -s -o /dev/null -w '%{http_code}' --max-time 3 http://127.0.0.1:18084/health/ready; }
echo "[$(ts)] estado inicial: consumidores=$(consumers) ready_worker=$(ready) outbox: $(outbox) $(net)"
docker stop cashflow-rabbitmq-1 >/dev/null; echo "[$(ts)] broker PARADO"
curl -sS -o /dev/null -w "[$(ts)] POST com broker parado -> %{http_code}\n" "${HA[@]}" -H 'Content-Type: application/json' -X POST "http://127.0.0.1:18080/api/v1/merchants/$M/transactions" -H "Idempotency-Key: f6-down-$(date +%s)" -d '{"type":"CREDIT","amount":"1.11"}'
echo "[$(ts)] tx-api ready=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18081/health/ready) (esperado 200: RNF-01)"
for s in 10 70; do sleep $((s==10?10:60)); echo "[$(ts)] +${s}s: ready_worker=$(ready) outbox: $(outbox)"; done
docker start cashflow-rabbitmq-1 >/dev/null; echo "[$(ts)] broker INICIADO"
for i in $(seq 1 60); do c=$(consumers); [ "$c" = 1 ] && [ "$(ready)" = 200 ] && break; sleep 2; done
echo "[$(ts)] consumidores=$c ready_worker=$(ready) apos ~$((i*2))s"
for i in $(seq 1 30); do o=$(outbox); case "$o" in pendentes=0*) break;; esac; sleep 2; done
echo "[$(ts)] outbox: $o $(net)"
