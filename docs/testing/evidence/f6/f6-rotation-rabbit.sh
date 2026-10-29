#!/usr/bin/env bash
# F6: rotacao das senhas do RabbitMQ (aplicacao e operacao) no .env local, SEM imprimir valores.
# NOTA (passada documental pos-revisao, 2026-10-29): este script foi EDITADO depois da execucao cujo log esta nesta pasta; as edicoes NAO foram reexecutadas
# PRE-REQUISITO: OVR = override scratch que entrega RABBITMQ_OPS_* ao broker (copia em scratch-rabbit-ops-override.yml, ajustar o caminho); BK = backup do .env fora do repositorio.
# LIMITE: 'rabbitmqctl authenticate_user <u> <senha>' recebe a senha em argv dentro do container (a CLI nao le stdin); aceitavel so em ambiente local descartavel.
# ARMADILHA verificada: variaveis exportadas no shell tem PRECEDENCIA sobre --env-file; por isso o .env so e lido em subshells,
# nunca exportado no shell que executa "docker compose" (a primeira tentativa deste teste rotacionou o arquivo mas nao os containers).
set -u
cd "$(dirname "$0")/../../../.." || exit 1
OVR="${OVR:?override que entrega RABBITMQ_OPS_* ao broker}"; BK="${BK:?caminho do backup do .env (fora do repositorio)}"
C="docker compose -f infra/compose/docker-compose.yml -f infra/compose/docker-compose.dev.yml -f $OVR --env-file infra/compose/.env"
[ -e "$BK" ] || cp infra/compose/.env "$BK"
envval() { ( set -a; . infra/compose/.env; set +a; printf '%s' "${!1}" ); }
newpw() { echo "rot-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"; }
setvar() { sed -i "s|^$1=.*|$1=$2|" infra/compose/.env; }
M=55555555-5555-4555-8555-555555555555; D=$(date -u +%F)
HA=(-H "X-Dev-Sub: u" -H "X-Dev-Client-Id: cashflow-web" -H "X-Dev-Merchant-Id: $M" -H "X-Dev-Scope: transactions.write transactions.read consolidation.read")
net() { curl -sS "${HA[@]}" "http://127.0.0.1:18082/api/v1/merchants/$M/consolidations/daily?date=$D" | grep -o '"dailyNet":"[^"]*"'; }
auth() { docker exec cashflow-rabbitmq-1 rabbitmqctl authenticate_user "$1" "$2" 2>&1 | tail -1 | grep -q Success && echo ok || echo recusada; }
OPS_USER=$(envval RABBITMQ_OPS_USER); APP_USER=$(envval RABBITMQ_DEFAULT_USER)
OLD_OPS=$(envval RABBITMQ_OPS_PASS); OLD_APP=$(envval RABBITMQ_DEFAULT_PASS)
echo "antes: dailyNet $(net)"
setvar RABBITMQ_OPS_PASS "$(newpw)"; setvar RABBITMQ_DEFAULT_PASS "$(newpw)"
NEW_OPS=$(envval RABBITMQ_OPS_PASS); NEW_APP=$(envval RABBITMQ_DEFAULT_PASS)
$C up -d --force-recreate rabbitmq transactions-api consolidation-worker 2>&1 | grep -c Recreated | sed 's/^/containers recriados: /'
for i in $(seq 1 60); do [ "$(docker inspect -f '{{.State.Health.Status}}' cashflow-consolidation-worker-1)" = healthy ] && [ "$(docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name | grep -c transaction-registered)" = 1 ] && break; sleep 3; done
echo "consumidor registrado apos ~$((i*3))s"
echo "authenticate_user ops: antiga=$(auth "$OPS_USER" "$OLD_OPS") nova=$(auth "$OPS_USER" "$NEW_OPS")"
echo "authenticate_user app: antiga=$(auth "$APP_USER" "$OLD_APP") nova=$(auth "$APP_USER" "$NEW_APP")"
echo "gestao HTTP: ops antiga=$(printf 'user = "%s:%s"\n' "$OPS_USER" "$OLD_OPS" | curl -s -K - -o /dev/null -w '%{http_code}' http://127.0.0.1:15672/api/overview) ops nova=$(printf 'user = "%s:%s"\n' "$OPS_USER" "$NEW_OPS" | curl -s -K - -o /dev/null -w '%{http_code}' http://127.0.0.1:15672/api/overview)"
curl -sS -o /dev/null -w 'POST -> %{http_code}\n' "${HA[@]}" -H 'Content-Type: application/json' -X POST "http://127.0.0.1:18080/api/v1/merchants/$M/transactions" -H "Idempotency-Key: f6-rot-$(date +%s)" -d '{"type":"CREDIT","amount":"0.01"}'
sleep 6; echo "depois: dailyNet $(net)"
echo "worker: recusas de acesso nos logs = $($C logs --since 2m consolidation-worker 2>&1 | grep -ci access_refused)"
