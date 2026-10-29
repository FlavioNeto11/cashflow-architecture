#!/usr/bin/env bash
# F7: sondas adicionais de itens herdados (decisions-needed.md linhas ~118 e ~123), pelo gateway, sem editar src/infra:
#  X1 caracteres de controle / NUL em `description` (item: "-> 500, rejeitar com 422")
#  X2 representacao do 201 fresco x GET do mesmo recurso (item: "201 omite description/reversedBy que o GET inclui")
#  X3 nenhuma linha/evento criado pelas requisicoes rejeitadas
# Datas: 2026-07-01 (sem dados previos). Chaves com sufixo unico por execucao.
# Uso: bash 15-extra-probes.sh > 15-extra-probes.log 2>&1 ; echo "exit=$?" >> 15-extra-probes.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
FAIL=0; RUN=$(date +%s); D=2026-07-01
echo "# F7 15 - sondas adicionais - $(date -u +%FT%TZ)"
mktokens
n0=$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and effective_date='$D'"); o0=$($PSQL_T -c "select count(*) from outbox_messages")
sec "X1. description com NUL / controle (JSON valido com escape \u0000)"
for esc in '\u0000' '\u0007' '\u001f' 'ok\u0000fim'; do
  c=$(post "$A" "$TAW" "f7-x1-$RUN-$RANDOM" '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'","description":"'"$esc"'"}')
  echo "  description=\"$esc\" -> HTTP $c | corpo: $(head -c 160 "$B" | tr -d '\r\n')"
done
echo "  (esperado pelo contrato: 422; o item herdado registra 500)"
n1=$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and effective_date='$D'"); o1=$($PSQL_T -c "select count(*) from outbox_messages")
echo "  linhas criadas por requisicoes com description invalida: $((n1-n0)) | linhas de outbox: $((o1-o0))"
sec "X2. 201 fresco x GET"
c=$(post "$A" "$TAW" "f7-x2-$RUN" '{"type":"CREDIT","amount":"3.21","effectiveDate":"'$D'","description":"f7-x2"}'); echo "  POST -> $c"
python -I -c 'import json,sys; d=json.load(open(sys.argv[1],encoding="utf-8")); print("  campos do 201:", sorted(d)); print(d["id"])' "$B" > "$S/x2.out"; head -1 "$S/x2.out"; ID=$(sed -n 2p "$S/x2.out" | tr -d '\r')
curl -sS $REVOKE --cacert "$CA" -o "$S/get.json" -w '  GET por id -> HTTP %{http_code}\n' "$GW/merchants/$A/transactions/$ID" -H "Authorization: Bearer $TA"
python -I -c 'import json,sys; d=json.load(open(sys.argv[1],encoding="utf-8")); print("  campos do GET:", sorted(d))' "$S/get.json"
echo "  diferenca de campos (201 vs GET): $(python -I -c '
import json,sys
a=set(json.load(open(sys.argv[1],encoding="utf-8"))); b=set(json.load(open(sys.argv[2],encoding="utf-8"))); print("so no GET:",sorted(b-a),"| so no 201:",sorted(a-b))' "$B" "$S/get.json")"
echo "FALHAS=$FAIL (sondas descritivas: nao ha criterio de passa/falha automatico; o relatorio interpreta)"
