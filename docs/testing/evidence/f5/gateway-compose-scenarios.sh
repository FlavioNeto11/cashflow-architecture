#!/usr/bin/env bash
# F5: stack do ZERO com o Compose BASE (sem docker-compose.dev.yml) e cenarios de seguranca pelo gateway (security.md 5, 6,
# 12): token Client Credentials REAL do Keycloak, POST/GET pelo gateway (201/200), negativos 401/403/404/413/429, chamada
# direta as APIs (porta mTLS) sem certificado e com cada certificado negativo, blocklist de thumbprint recarregada sem
# reinicio. Tokens e segredos NUNCA sao impressos (so claims selecionadas e codigos de status).
# Uso (na raiz do repositorio; requer Docker, openssl >= 3.4, curl):
#   bash docs/testing/evidence/f5/gateway-compose-scenarios.sh 2>&1 | tee docs/testing/evidence/f5/compose-gateway-scenarios.log
set -u
cd "$(dirname "${BASH_SOURCE[0]}")/../../../.."
C="docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env"
GW=https://127.0.0.1:8443/api/v1
KC=http://127.0.0.1:8080/realms/cashflow/protocol/openid-connect/token
CA=infra/pki/out/ca.crt
A=33333333-3333-4333-8333-333333333333
BM=44444444-4444-4444-8444-444444444444
TODAY=$(date -u +%F)
S=$(mktemp -d); command -v cygpath >/dev/null 2>&1 && S=$(cygpath -m "$S"); H="$S/h.txt"; B="$S/b.json"  # caminho Windows para o curl nativo
PKI_WIN="$(command -v cygpath >/dev/null 2>&1 && cygpath -m "$PWD/infra/pki/out" || echo "$PWD/infra/pki/out")"
CURL_IMG=curlimages/curl:8.14.1
BLOCKLIST=infra/pki/out/blocked-thumbprints.txt
# curl do Git for Windows usa Schannel, que exige checar revogacao: a CA local nao publica CRL/OCSP (GAP declarado em
# security.md 6), logo sem esta flag o cliente falha com "revocation status is unknown". Nao afeta o servidor nem o
# gateway (que valida as APIs com .NET); e so o verificador do Windows. Em Linux/OpenSSL a flag nao existe e fica vazia.
REVOKE=""; curl --version | grep -q Schannel && REVOKE=--ssl-revoke-best-effort

ts() { date -u +%H:%M:%S; }
sec() { echo; echo "=================== [$(ts)] $*"; }
# req <rotulo> <curl args...>: imprime status, cabecalhos relevantes e inicio do corpo (nunca o Authorization)
req() {
  local label="$1"; shift
  echo "--- [$(ts)] $label"
  curl -sS $REVOKE --cacert "$CA" -D "$H" -o "$B" "$@"
  echo "status: $(head -1 "$H" | tr -d '\r')"
  grep -iE '^(www-authenticate|retry-after|idempotency-replayed|server|x-content-type-options|cache-control|referrer-policy|content-type):' "$H" | tr -d '\r' | sed 's/^/  hdr: /'
  echo "  body: $(head -c 400 "$B" | tr -d '\n')"
}
# token <client_id> <secret> [scope]: devolve o access_token (nao imprime)
token() {
  local args=(-d grant_type=client_credentials -d "client_id=$1" -d "client_secret=$2")
  [ -n "${3:-}" ] && args+=(-d "scope=$3")
  curl -sS "${args[@]}" "$KC" | sed -E 's/.*"access_token":"([^"]+)".*/\1/'
}
# claims <token>: imprime so iss, aud, azp, merchant_id, scope e vida (nunca o token)
claims() {
  local p; p=$(echo "$1" | cut -d. -f2 | tr '_-' '/+'); p="$p$(printf '%*s' $(( (4 - ${#p} % 4) % 4 )) '' | tr ' ' '=')"
  local j exp iat; j=$(echo "$p" | base64 -d 2>/dev/null)
  exp=$(echo "$j" | grep -oE '"exp":[0-9]+' | cut -d: -f2); iat=$(echo "$j" | grep -oE '"iat":[0-9]+' | cut -d: -f2)
  echo "  vida=$(( ${exp:-0} - ${iat:-0} )) s"
  echo "$j" | grep -oE '"(iss|aud|azp|merchant_id|scope|typ)":("[^"]*"|\[[^]]*\])' | sed 's/^/  /'
}
# direct <rotulo> <curl args no container>: curl na rede app (como um processo interno), mostra codigo de saida e alerta TLS
direct() {
  local label="$1"; shift
  echo "--- [$(ts)] direto (rede app): $label"
  local out rc
  out=$(MSYS_NO_PATHCONV=1 docker run --rm --network cashflow_app -v "$PKI_WIN:/certs:ro" "$CURL_IMG" \
    -sS -v --max-time 15 -o /dev/null -w 'http=%{http_code}' "$@" 2>&1); rc=$?
  echo "  curl exit=$rc $(echo "$out" | grep -oE 'http=[0-9]+' | tail -1)"
  echo "$out" | grep -iE 'alert|SSL|TLS|certificate|handshake|error|refused|reset|Empty reply' | grep -viE 'CAfile|CApath|successfully set|ALPN|^\* *TLSv1\.[23] \((OUT|IN)\), TLS (handshake|change cipher)' | head -4 | sed 's/^/  /'
}

trap 'echo; echo "limpeza: blocklist restaurada (vazia)"; printf "# thumbprints SHA-1 bloqueados (um por linha)\n" > "$BLOCKLIST"; rm -rf "$S"' EXIT

sec "0. PKI local (infra/pki/make-certs.sh; no-op se ja existe) e .env (valores nunca impressos)"
bash infra/pki/make-certs.sh
set -a; . <(tr -d '\r' < infra/compose/.env); set +a   # tr: .env salvo com CRLF no Windows deixaria \r no segredo
for v in KEYCLOAK_MERCHANT_A_SYSTEM_SECRET KEYCLOAK_MERCHANT_B_SYSTEM_SECRET; do
  eval "x=\${$v:-}"; [ -n "$x" ] && echo "$v: definido (${#x} chars)" || { echo "$v: AUSENTE em infra/compose/.env"; exit 1; }
done

sec "1. Stack do zero: down -v + up -d --build (Compose BASE: gateway 127.0.0.1:8443, APIs sem porta publicada)"
$C down -v --remove-orphans >/dev/null 2>&1
$C up -d --build 2>&1 | grep -vE '^\s*$' | tail -25
for i in $(seq 1 120); do
  st="$(docker inspect -f '{{.State.Health.Status}}' cashflow-gateway-1 cashflow-transactions-api-1 cashflow-consolidation-api-1 2>/dev/null | tr '\n' ' ')"
  init="$(docker inspect -f '{{.State.Status}}/{{.State.ExitCode}}' cashflow-keycloak-init-1 2>/dev/null)"
  [ "$st" = "healthy healthy healthy " ] && [ "$init" = "exited/0" ] && break
  sleep 3
done
echo "[$(ts)] saude gateway/tx/cons: $st | keycloak-init: $init (apos ~$((i*3)) s)"
$C ps --format 'table {{.Name}}\t{{.Status}}\t{{.Ports}}'
echo "--- keycloak-init:"; docker logs cashflow-keycloak-init-1 2>&1 | tail -3

sec "2. Tokens Client Credentials no Keycloak publicado (127.0.0.1:8080); KC_HOSTNAME fixo => iss=http://keycloak:8080/realms/cashflow"
TA=$(token merchant-a-system "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" "transactions.write transactions.read consolidation.read")
TAW=$(token merchant-a-system "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" "transactions.write")
TB=$(token merchant-b-system "$KEYCLOAK_MERCHANT_B_SYSTEM_SECRET" "transactions.write transactions.read consolidation.read")
echo "token A (3 escopos): ${#TA} chars"; claims "$TA"
echo "token A (so write): ${#TAW} chars"; claims "$TAW" | grep -E 'scope|merchant'
echo "token B: ${#TB} chars"; claims "$TB" | grep -E 'merchant|azp'
echo "--- sem segredo correto:"; curl -sS -o /dev/null -w 'status: %{http_code}\n' -d grant_type=client_credentials -d client_id=merchant-a-system -d client_secret=errado "$KC"

sec "3. Caminho feliz pelo gateway (TLS de borda da CA local; mTLS gateway->APIs por dentro)"
K1=$(openssl rand -hex 16)
req "POST transacao A (201)" -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TA" -H "Idempotency-Key: $K1" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"64.80"}'
TXID=$(sed -E 's/.*"id":"([^"]+)".*/\1/' "$B")
req "replay mesma chave (201 + Idempotency-Replayed)" -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TA" -H "Idempotency-Key: $K1" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"64.80"}'
req "GET transacao por id (200)" "$GW/merchants/$A/transactions/$TXID" -H "Authorization: Bearer $TA"
req "GET consolidado diario A (200)" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TA"
req "POST transacao B (201)" -X POST "$GW/merchants/$BM/transactions" -H "Authorization: Bearer $TB" -H "Idempotency-Key: $(openssl rand -hex 16)" -H 'Content-Type: application/json' -d '{"type":"DEBIT","amount":"10.00"}'

sec "4. Negativos na borda (ADR-0006: 401 / 403 / 404 identico / 413)"
req "sem token (401 + WWW-Authenticate)" -X POST "$GW/merchants/$A/transactions" -H "Idempotency-Key: x" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00"}'
req "token adulterado (401)" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer ${TA%?}x"
req "alg=none (401)" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $(printf '{"alg":"none","typ":"JWT"}' | base64 -w0 | tr '+/' '-_' | tr -d '=').$(echo "$TA" | cut -d. -f2)."
req "escopo errado: token so write em consolidado (403)" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TAW"
req "cross-tenant: token A em POST de B (404)" -X POST "$GW/merchants/$BM/transactions" -H "Authorization: Bearer $TA" -H "Idempotency-Key: y" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00"}'
req "cross-tenant: token A le consolidado de B (404)" "$GW/merchants/$BM/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TA"
req "recurso inexistente do proprio A (404, mesmo corpo)" "$GW/merchants/$A/transactions/00000000-0000-4000-8000-000000000000" -H "Authorization: Bearer $TA"
req "X-Dev-* sem token (401: cabecalhos de dev nao autenticam)" -X POST "$GW/merchants/$A/transactions" -H "X-Dev-Sub: intruso" -H "X-Dev-Client-Id: x" -H "X-Dev-Merchant-Id: $A" -H "X-Dev-Scope: transactions.write" -H "Idempotency-Key: z" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00"}'
head -c 17408 /dev/zero | tr '\0' 'x' > "$S/big.txt"
req "corpo 17 KB (413, antes de autenticar)" -X POST "$GW/merchants/$A/transactions" -H 'Content-Type: application/json' --data-binary "@$S/big.txt"
req "/health/ready pelo gateway (404: saude nao e roteada)" https://127.0.0.1:8443/health/ready -H "Authorization: Bearer $TA"
req "GET lista sem transactions.read (403)" "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TAW"

sec "5. Rate limit por merchant no consolidado (padrao 60/s por merchant, 120/s global; 200 GETs em paralelo como A)"
export GW TA A TODAY CA REVOKE
T0=$(date +%s)
seq 1 200 | xargs -P 60 -I{} sh -c 'curl -sS $REVOKE --cacert "$CA" -o /dev/null -w "%{http_code}\n" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TA"' | sort | uniq -c | sed 's/^/  /'
T1=$(date +%s); echo "  200 requisicoes em $((T1-T0)) s (~$(( 200 / ( (T1-T0) > 0 ? (T1-T0) : 1 ) )) req/s do host). Se nao houve 429, o gerador (um processo curl por requisicao no Windows) nao atingiu 60 req/s: o 429 por merchant esta validado em processo (T-28, limite 3/s) e a medicao com os numeros do Compose fica para T-08 (k6, F7)."
req "B logo em seguida (200: balde independente)" "$GW/merchants/$BM/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TB"

sec "6. Direto as APIs na rede app (porta mTLS 8080): so o certificado do gateway completa o handshake"
direct "sem certificado de cliente" --cacert /certs/ca.crt https://transactions-api:8080/api/v1
for n in neg-foreign-ca neg-expired neg-wrong-san neg-wrong-eku; do
  direct "certificado $n" --cacert /certs/ca.crt --cert /certs/$n.crt --key /certs/$n.key https://transactions-api:8080/api/v1
done
direct "HTTP puro na porta TLS" http://transactions-api:8080/api/v1
direct "certificado do gateway, sem token (handshake OK -> 401 da API)" --cacert /certs/ca.crt --cert /certs/gateway-client.crt --key /certs/gateway-client.key "https://transactions-api:8080/api/v1/merchants/$A/transactions"
direct "certificado do gateway + token A (handshake OK -> 200 da consolidation-api)" --cacert /certs/ca.crt --cert /certs/gateway-client.crt --key /certs/gateway-client.key -H "Authorization: Bearer $TA" "https://consolidation-api:8080/api/v1/merchants/$A/consolidations/daily?date=$TODAY"
direct "porta de saude 8081 sem TLS: /health/ready" http://transactions-api:8081/health/ready
direct "porta de saude 8081: rota de negocio (404)" "http://transactions-api:8081/api/v1/merchants/$A/transactions"

sec "7. Revogacao local: thumbprint do gateway na blocklist (recarregada sem reinicio) e volta"
TP=$(openssl x509 -in infra/pki/out/gateway-client.crt -noout -fingerprint -sha1 | sed 's/.*=//; s/://g')
echo "thumbprint gateway-client: ${TP:0:8}... (${#TP} hex)"
printf '# bloqueado pelo roteiro F5\n%s\n' "$TP" > "$BLOCKLIST"; sleep 3
direct "gateway bloqueado: handshake NOVO recusado" --cacert /certs/ca.crt --cert /certs/gateway-client.crt --key /certs/gateway-client.key https://transactions-api:8080/api/v1
req "pelo gateway com o cliente bloqueado, conexao do pool ainda aberta (200 esperado: o handshake nao se repete; PooledConnectionLifetime=2 min limita a janela)" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TA"
echo "--- [$(ts)] docker compose restart gateway (forca conexoes novas; equivale a esperar o fim da vida do pool)"
$C restart gateway >/dev/null 2>&1
for i in $(seq 1 30); do [ "$(docker inspect -f '{{.State.Health.Status}}' cashflow-gateway-1 2>/dev/null)" = healthy ] && break; sleep 2; done
req "pelo gateway apos restart, cliente bloqueado (502, sem vazar o destino)" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TA"
printf '# thumbprints SHA-1 bloqueados (um por linha)\n' > "$BLOCKLIST"; sleep 3
req "blocklist limpa: pelo gateway de novo (200)" "$GW/merchants/$A/consolidations/daily?date=$TODAY" -H "Authorization: Bearer $TA"

sec "8. Logs de seguranca (sem token): gateway e transactions-api"
$C logs --no-log-prefix gateway 2>/dev/null | grep -E '"Category":"CashFlow.Hosting.(Security|Tls)"|Gateway' | grep -oE '"Message":"[^"]{0,200}' | sort | uniq -c | sort -rn | head -12
$C logs --no-log-prefix transactions-api 2>/dev/null | grep -E '"Category":"CashFlow.Hosting.Tls"' | grep -oE '"Message":"[^"]{0,160}' | sort | uniq -c | sort -rn | head -8
echo; echo "[$(ts)] fim. Stack permanece de pe (Compose base)."
