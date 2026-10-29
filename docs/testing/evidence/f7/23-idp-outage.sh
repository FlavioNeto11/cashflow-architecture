#!/usr/bin/env bash
# F7 (T-32, correcao pos-revisao 12b): IdP (Keycloak) indisponivel com a stack de pe. Hipotese de ADR/security: tokens ja emitidos continuam
# validos no gateway enquanto o cache de metadados/JWKS vale e o token nao expira; obter token NOVO falha. Registra a prontidao (health) de gateway,
# APIs e worker durante a queda. Parada MANUAL (`compose stop`): o Compose nao a reinicia sozinho; o roteiro faz `compose start` ao final.
# Os tokens nunca sao impressos. Janela total < 5 min (vida padrao do access token do realm; o roteiro confere o `exp` do token em uso).
# EFEITO COLATERAL: cria 2 lancamentos CREDIT de 1,00 do merchant A em 2026-03-11 (dados sinteticos de DEV).
# (run1 foi descartada: sem MSYS_NO_PATHCONV o caminho /app/entrypoint virava um caminho do Windows e a sonda de prontidao deu rc=127.)
# Uso: bash 23-idp-outage.sh > 23-idp-outage.log 2>&1 ; echo "exit=$?" >> 23-idp-outage.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
FAIL=0
expect() { if [ "$2" = "$3" ]; then echo "  OK: $1 = $3"; else echo "  FALHA: $1 esperado='$2' obtido='$3'"; FAIL=$((FAIL+1)); fi; }
RUN=$(date +%s); D=2026-03-11
exp_in() { python -I -c 'import sys,json,base64,time; p=sys.argv[1].split(".")[1]; p+="="*(-len(p)%4); print(int(json.loads(base64.urlsafe_b64decode(p))["exp"]-time.time()))' "$1"; }
ready() { # ready: estado de prontidao como o proprio healthcheck do container (exit code do /app/entrypoint --healthcheck) e o health do Docker
  for c in gateway transactions-api consolidation-api consolidation-worker; do
    MSYS_NO_PATHCONV=1 docker exec cashflow-$c-1 /app/entrypoint --healthcheck http://127.0.0.1:8081/health/ready >/dev/null 2>&1; rc=$?
    echo "    $c: ready_rc=$rc docker_health=$(docker inspect -f '{{.State.Health.Status}}' cashflow-$c-1)"
  done
}
echo "# F7 23 - IdP (Keycloak) indisponivel - $(date -u +%FT%TZ)"
sec "0. baseline com o Keycloak no ar"
mktokens
[ -n "$TAW" ] && [ -n "$TA" ] && echo "tokens obtidos (nao impressos); TAW expira em $(exp_in "$TAW")s" || { echo "PENDENTE: sem tokens; abortado"; exit 3; }
expect "POST baseline" 201 "$(post "$A" "$TAW" f7-idp-base-$RUN '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'","description":"f7-idp-base"}')"
expect "GET consolidado baseline" 200 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TA")"
ready
sec "1. docker compose stop keycloak"
$C stop keycloak 2>&1 | sed 's/^/  /'; T0=$(date +%s)
echo "keycloak: $(docker inspect -f '{{.State.Status}} exit={{.State.ExitCode}}' cashflow-keycloak-1)"
for dt in 3 35; do
  while [ $(( $(date +%s) - T0 )) -lt $dt ]; do sleep 1; done
  sec "2.$dt  +${dt}s com o IdP parado (token em cache: expira em $(exp_in "$TAW")s)"
  expect "POST com token ja emitido" 201 "$(post "$A" "$TAW" f7-idp-$dt-$RUN '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'","description":"f7-idp"}')"
  expect "GET consolidado com token ja emitido" 200 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TA")"
  NT=$(token merchant-a-system "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" "transactions.write" 2>/dev/null)
  case "$NT" in ""|*" "*|*"{"*|*"<"*) echo "  OK: token NOVO nao foi obtido (Keycloak indisponivel)";; *) if [ "$(echo "$NT" | cut -c1-3)" = eyJ ]; then echo "  FALHA: obteve token novo com o IdP parado"; FAIL=$((FAIL+1)); else echo "  OK: token NOVO nao foi obtido (resposta nao e um JWT, ${#NT} chars)"; fi;; esac
  NTc=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 -d grant_type=client_credentials -d client_id=merchant-a-system -d "client_secret=$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" "$KC" 2>/dev/null); echo "  token novo: http=$NTc (000 = conexao recusada)"
  expect "request sem token continua 401 (gateway nao depende do IdP para recusar)" 401 "$(code "$GW/merchants/$A/consolidations/daily?date=$D")"
  echo "  prontidao:"; ready
done
sec "3. docker compose start keycloak e recuperacao"
$C start keycloak 2>&1 | sed 's/^/  /'
wait_healthy cashflow-keycloak-1 180
mktokens
expect "token novo apos o retorno do IdP" "yes" "$([ "$(echo "$TAW" | cut -c1-3)" = eyJ ] && echo yes || echo no)"
expect "POST com token novo" 201 "$(post "$A" "$TAW" f7-idp-after-$RUN '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'","description":"f7-idp-after"}')"
converge "$A" "$TA" "$D" 60
echo "  prontidao final:"; ready
echo "estado da stack:"; $C ps --format 'table {{.Name}}\t{{.Status}}' | sed 's/^/  /'
echo "FALHAS=$FAIL"; [ "$FAIL" = 0 ]
