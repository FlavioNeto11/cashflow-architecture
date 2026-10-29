#!/usr/bin/env bash
# F6: rotacao da senha do papel cons_worker (PostgreSQL) no .env local e no servidor, SEM imprimir valores.
# NOTA (passada documental pos-revisao, 2026-10-29): este script foi EDITADO depois da execucao cujo log esta nesta pasta; as edicoes NAO foram reexecutadas
# PRE-REQUISITO: OVR = override scratch que entrega RABBITMQ_OPS_* ao broker (copia em scratch-rabbit-ops-override.yml, ajustar o caminho); sem ele o script aborta.
# (-h postgres: o loopback do container e "trust" no pg_hba da imagem, entao -h 127.0.0.1 nao valida senha.)
# Mostra tambem o fato verificavel: mudar so o .env NAO altera o banco (senha so e aplicada no init do volume).
set -u
cd "$(dirname "$0")/../../../.." || exit 1
OVR="${OVR:?override que entrega RABBITMQ_OPS_* ao broker}"
C="docker compose -f infra/compose/docker-compose.yml -f infra/compose/docker-compose.dev.yml -f $OVR --env-file infra/compose/.env"
envval() { ( set -a; . infra/compose/.env; set +a; printf '%s' "${!1}" ); }
newpw() { echo "rot-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"; }
login() { PGPASSWORD="$1" docker exec -i -e PGPASSWORD cashflow-postgres-1 psql -h postgres -U cons_worker -d consolidation_db -At -c 'SELECT 1' 2>&1 | head -3 | tr '\n' ' '; }
M=55555555-5555-4555-8555-555555555555; D=$(date -u +%F)
HA=(-H "X-Dev-Sub: u" -H "X-Dev-Client-Id: cashflow-web" -H "X-Dev-Merchant-Id: $M" -H "X-Dev-Scope: transactions.write transactions.read consolidation.read")
net() { curl -sS "${HA[@]}" "http://127.0.0.1:18082/api/v1/merchants/$M/consolidations/daily?date=$D" | grep -o '"dailyNet":"[^"]*"'; }
OLD=$(envval CONS_WORKER_PASSWORD)
NEW=$(newpw)
sed -i "s|^CONS_WORKER_PASSWORD=.*|CONS_WORKER_PASSWORD=$NEW|" infra/compose/.env
echo "1. so o .env mudou: login com senha nova no servidor = $(login "$NEW")  (esperado: falha de autenticacao)"
echo "   login com senha antiga = $(login "$OLD")"
# 2. ALTER ROLE como superusuario; o valor entra pela variavel de ambiente do processo psql, nao pela linha de comando do psql.
printf 'ALTER ROLE cons_worker PASSWORD :'"'"'pw'"'"';\n' | PW="$NEW" docker exec -i -e PW cashflow-postgres-1 sh -c 'psql -U postgres -d postgres -v ON_ERROR_STOP=1 -v pw="$PW"'
echo "2. apos ALTER ROLE: senha nova = $(login "$NEW") | antiga = $(login "$OLD")"
$C up -d --force-recreate --no-deps consolidation-worker 2>&1 | grep -c Recreated | sed 's/^/3. containers recriados: /'
for i in $(seq 1 60); do [ "$(docker inspect -f '{{.State.Health.Status}}' cashflow-consolidation-worker-1)" = healthy ] && [ "$(docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_consumers queue_name | grep -c transaction-registered)" = 1 ] && break; sleep 3; done
echo "   worker healthy + consumidor registrado apos ~$((i*3))s"
echo "antes: $(net)"
curl -sS -o /dev/null -w 'POST -> %{http_code}\n' "${HA[@]}" -H 'Content-Type: application/json' -X POST "http://127.0.0.1:18080/api/v1/merchants/$M/transactions" -H "Idempotency-Key: f6-rotpg-$(date +%s)" -d '{"type":"CREDIT","amount":"0.02"}'
sleep 6; echo "depois: $(net)"
