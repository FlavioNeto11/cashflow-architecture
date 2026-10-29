#!/usr/bin/env bash
# F7: funcoes comuns dos roteiros (source, nao executar). Stack Compose BASE ja de pe (sem overrides).
# Segredos do infra/compose/.env sao carregados no ambiente do processo e NUNCA impressos; tokens JWT nunca sao impressos
# (so tamanho e claims selecionadas). Requer: docker, curl, openssl, python (stdlib), bash.
set -u
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cd "$REPO" || exit 1
C="docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env"
GWH=https://127.0.0.1:8443            # entrada publicada do host (Docker Desktop encaminha para gateway:8080)
GW=$GWH/api/v1
KC=http://127.0.0.1:8080/realms/cashflow/protocol/openid-connect/token
MQ=http://127.0.0.1:15672/api
CA=infra/pki/out/ca.crt
A=33333333-3333-4333-8333-333333333333
BM=44444444-4444-4444-8444-444444444444
TODAY=$(date -u +%F)
S=$(mktemp -d); command -v cygpath >/dev/null 2>&1 && S=$(cygpath -m "$S")
H="$S/h.txt"; B="$S/b.json"
PKI_WIN="$(command -v cygpath >/dev/null 2>&1 && cygpath -m "$PWD/infra/pki/out" || echo "$PWD/infra/pki/out")"
CURL_IMG=curlimages/curl:8.14.1
# curl do Git for Windows usa Schannel e exige checar revogacao (CA local sem CRL/OCSP, GAP de security.md 6)
REVOKE=""; curl --version | grep -q Schannel && REVOKE=--ssl-revoke-best-effort
PSQL_T="docker exec -i cashflow-postgres-1 psql -U migrator -d transactions_db -v ON_ERROR_STOP=1 -At"
PSQL_C="docker exec -i cashflow-postgres-1 psql -U migrator -d consolidation_db -v ON_ERROR_STOP=1 -At"

set -a; . <(tr -d '\r' < infra/compose/.env); set +a
trap 'rm -rf "$S"' EXIT

ts() { date -u +%H:%M:%S.%3N; }
sec() { echo; echo "=================== [$(ts)] $*"; }
# req <rotulo> <curl args...>: status, cabecalhos relevantes e inicio do corpo (nunca o Authorization)
req() {
  local label="$1"; shift
  echo "--- [$(ts)] $label"
  curl -sS $REVOKE --cacert "$CA" -D "$H" -o "$B" "$@" || echo "  curl falhou rc=$?"
  echo "status: $(head -1 "$H" 2>/dev/null | tr -d '\r')"
  grep -iE '^(www-authenticate|retry-after|idempotency-replayed|server|x-content-type-options|cache-control|referrer-policy|content-type|x-frame-options|content-security-policy|strict-transport-security|permissions-policy|cross-origin-[a-z-]+):' "$H" 2>/dev/null | tr -d '\r' | sed 's/^/  hdr: /'
  echo "  body: $(head -c 300 "$B" 2>/dev/null | tr -d '\n')"
}
# code <curl args...>: so o codigo HTTP
code() { curl -sS $REVOKE --cacert "$CA" -o /dev/null -w '%{http_code}' "$@"; }
# token <client_id> <secret> [scope]: devolve o access_token (nao imprime)
token() {
  local args=(-d grant_type=client_credentials -d "client_id=$1" -d "client_secret=$2")
  [ -n "${3:-}" ] && args+=(-d "scope=$3")
  curl -sS "${args[@]}" "$KC" | sed -E 's/.*"access_token":"([^"]+)".*/\1/'
}
claims() {
  local p; p=$(echo "$1" | cut -d. -f2 | tr '_-' '/+'); p="$p$(printf '%*s' $(( (4 - ${#p} % 4) % 4 )) '' | tr ' ' '=')"
  local j; j=$(echo "$p" | base64 -d 2>/dev/null)
  echo "$j" | grep -oE '"(iss|aud|azp|merchant_id|scope|typ)":("[^"]*"|\[[^]]*\])' | sed 's/^/  /'
}
mktokens() {
  SCOPES="transactions.write transactions.read consolidation.read"
  TA=$(token merchant-a-system "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" "$SCOPES")
  TB=$(token merchant-b-system "$KEYCLOAK_MERCHANT_B_SYSTEM_SECRET" "$SCOPES")
  TAW=$(token merchant-a-system "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" "transactions.write")
}
# mq <curl args...>: API de gestao do RabbitMQ com o usuario de OPERACAO (credencial entregue por stdin ao curl, fora do argv)
mq() { printf 'user = "%s:%s"\n' "$RABBITMQ_OPS_USER" "$RABBITMQ_OPS_PASS" | curl -sS -K - -H 'content-type: application/json' "$@"; }
# filas via rabbitmqctl (estado imediato do no, sem o atraso de estatisticas da API de gestao)
qctl() { docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name messages_ready messages_unacknowledged consumers 2>&1 | tr '\t' ' ' | sed 's/^/  rabbitmqctl: /'; }
# post <merchant> <token> <key> <json>: imprime so o codigo HTTP; corpo em $B, cabecalhos em $H
post() { curl -sS $REVOKE --cacert "$CA" -D "$H" -o "$B" -w '%{http_code}' -X POST "$GW/merchants/$1/transactions" -H "Authorization: Bearer $2" -H "Idempotency-Key: $3" -H 'Content-Type: application/json' -d "$4"; }
# daily <merchant> <token> <date> [extra query]: corpo do consolidado
daily() { curl -sS $REVOKE --cacert "$CA" "$GW/merchants/$1/consolidations/daily?date=$3${4:-}" -H "Authorization: Bearer $2"; }
jfield() { python -I -c 'import json,sys; d=json.load(sys.stdin); print(d.get(sys.argv[1]))' "$1" 2>/dev/null; }
# fonte (oracula): credito/debito/contagem de lancamentos no PostgreSQL operacional para merchant+data
src() { $PSQL_T -F ' ' -c "SELECT coalesce(sum(CASE WHEN entry_type=1 THEN amount END),0)::numeric(24,2), coalesce(sum(CASE WHEN entry_type=2 THEN amount END),0)::numeric(24,2), count(*) FROM transactions WHERE merchant_id='$1' AND effective_date='$2'"; }
# proj: linha da projecao (daily_consolidations) para merchant+data
proj() { $PSQL_C -F ' ' -c "SELECT credits, debits, entries_count FROM daily_consolidations WHERE merchant_id='$1' AND effective_date='$2'"; }
# converge <merchant> <token> <date> <timeout s>: espera API do consolidado == fonte (credits debits count); imprime resultado
converge() {
  local m="$1" t="$2" d="$3" max="${4:-90}" s api i
  for i in $(seq 1 "$max"); do
    s=$(src "$m" "$d"); api=$(daily "$m" "$t" "$d" | python -I -c 'import json,sys; d=json.load(sys.stdin); print(d["credits"],d["debits"],d["entriesCount"])' 2>/dev/null)
    [ -n "$api" ] && [ "$s" = "$api" ] && break; sleep 1
  done
  echo "[$(ts)] converge($d): fonte='$s' consolidado='${api:-<indisponivel>}' apos ${i}s -> $([ "$s" = "${api:-x}" ] && echo IGUAL || echo DIVERGENTE)"
}
wait_healthy() { # wait_healthy <container> [max s]
  local i; for i in $(seq 1 "${2:-120}"); do [ "$(docker inspect -f '{{.State.Health.Status}}' "$1" 2>/dev/null)" = healthy ] && break; sleep 1; done
  echo "[$(ts)] $1 health=$(docker inspect -f '{{.State.Health.Status}}' "$1" 2>/dev/null) apos ${i}s"
}
