#!/usr/bin/env bash
# F7 Passo B (consequencia do PF-03): a secao 7 do 02 usava NUL em `description` como gerador de 5xx da API; com PF-03 corrigido ele
# da 422 e a verificacao antiga deixou de servir. Esta rodada cobre a MESMA asserção de cabecalhos de seguranca em dois 5xx:
#   (1) 500 gerado PELA API e repassado pelo gateway, com UTF-8 invalido no corpo (achado PF-17; some quando PF-17 for corrigido);
#   (2) 502 gerado PELO gateway, com a consolidation-api parada por alguns segundos (estavel; nao depende de defeito).
# Uso: bash 21-07-5xx-headers.sh > 21-07-5xx-headers.log 2>&1 ; echo "exit=$?" >> ...
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
FAIL=0; D=2026-03-17
expect() { if [ "$2" = "$3" ]; then echo "  [OK]    $1 (esperado $2, obtido $3)"; else echo "  [FALHA] $1 (esperado $2, obtido $3)"; FAIL=$((FAIL+1)); fi; }
hdr_assert() {
  expect "$1: status" "$2" "$(head -1 "$H" | tr -d '\r' | awk '{print $2}')"
  expect "$1: Server ausente" 0 "$(grep -ci '^server:' "$H")"
  expect "$1: X-Content-Type-Options nosniff" 1 "$(grep -ciE '^x-content-type-options: *nosniff' "$H")"
  expect "$1: Cache-Control no-store" 1 "$(grep -ciE '^cache-control:.*no-store' "$H")"
  expect "$1: Referrer-Policy no-referrer" 1 "$(grep -ciE '^referrer-policy: *no-referrer' "$H")"
}
echo "# F7 21-07 - cabecalhos de seguranca em 5xx - $(date -u +%FT%TZ)"
mktokens
sec "1. 500 da API via gateway (byte E7 isolado = UTF-8 invalido, PF-17)"
printf '{"type":"CREDIT","amount":"1.00","effectiveDate":"%s","description":"a\xe7b"}' "$D" > "$S/bad.json"
req "500 da API" -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TAW" -H "Idempotency-Key: f7-21-07-$RANDOM-$RANDOM" -H 'Content-Type: application/json' --data-binary @"$S/bad.json"
hdr_assert "500 da API via gateway" 500
sec "2. 502 do gateway (consolidation-api parada)"
$C stop consolidation-api 2>&1 | tail -1
req "502 do gateway" "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TA"
hdr_assert "502 do gateway" 502
$C start consolidation-api 2>&1 | tail -1
wait_healthy cashflow-consolidation-api-1 120
req "controle: consolidado volta a 200" "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TA"
expect "controle apos religar: status" 200 "$(head -1 "$H" | tr -d '\r' | awk '{print $2}')"
echo "FALHAS=$FAIL"; exit $FAIL
