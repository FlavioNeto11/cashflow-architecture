#!/usr/bin/env bash
# F9 (item 15 da auditoria final; T-26/T-25): token VALIDO do Keycloak, com os escopos certos, mas SEM a claim merchant_id.
# Esperado: o gateway/APIs recusam (nenhum 2xx, nenhum 5xx) e nada e gravado. Client temporario criado e removido aqui (trap).
# Uso: bash docs/testing/evidence/f9/13-token-sem-merchant.sh > docs/testing/evidence/f9/13-token-sem-merchant.log 2>&1
. "$(dirname "${BASH_SOURCE[0]}")/../f7/f7-common.sh"
. "$REPO/docs/testing/evidence/f9/f9-kc.sh"
CID=f9-sem-merchant-$(date -u +%H%M%S); D=2026-08-21; FAIL=0
expect() { if [ "$2" = "$3" ]; then echo "  OK   $1: $3"; else echo "  FALHA $1: esperado $2, obtido $3"; FAIL=$((FAIL+1)); fi; }
echo "# F9 13 - token sem merchant_id - $(date -u +%FT%TZ)"
echo "gateway: imagem $(docker inspect -f '{{.Image}}' cashflow-gateway-1 | cut -c1-19) iniciado em $(docker inspect -f '{{.State.StartedAt}}' cashflow-gateway-1)"
export KC_NEW_SECRET; KC_NEW_SECRET=$(kc_secret)
trap 'echo "remocao do client temporario $CID: http=$(kc_delete "$CID")"; rm -rf "$S"' EXIT
echo "criacao do client temporario $CID (copia de merchant-a-system SEM o mapper merchant_id): http=$(kc_create "$CID" -)"
T=$(token "$CID" "$KC_NEW_SECRET" "transactions.write transactions.read consolidation.read")
echo "token: ${#T} chars; claims selecionadas:"; claims "$T"
echo "  merchant_id presente no token? $(claims "$T" | grep -q merchant_id && echo SIM || echo NAO)"
N0=$($PSQL_T -c "select count(*) from transactions where effective_date='$D'")
expect "POST lancamento (merchant A na rota)" 403 "$(code -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $T" -H "Idempotency-Key: f9-13-$CID" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'","description":"f9-13"}')"
expect "GET consolidado diario (merchant A)" 403 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $T")"
expect "GET lancamento inexistente (merchant A)" 403 "$(code "$GW/merchants/$A/transactions/00000000-0000-4000-8000-000000000000" -H "Authorization: Bearer $T")"
req "corpo e cabecalhos de uma recusa" "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $T"
expect "lancamentos gravados na data $D" "$N0" "$($PSQL_T -c "select count(*) from transactions where effective_date='$D'")"
echo "FALHAS=$FAIL"
[ "$FAIL" = 0 ]
