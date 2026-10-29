#!/usr/bin/env bash
# F7 (T-03, T-06, T-21, RF-06, RF-08): idempotencia, corrida de POSTs iguais, lost update e estorno concorrente, tudo pelo
# gateway REAL e com oraculo no PostgreSQL (fonte) e no consolidado (projecao). Cada corrida usa data e rotulo proprios.
# Os POSTs sao limitados pelo gateway (20/s sustentado, burst 40 por merchant): as corridas respeitam n <= 40 com pausa de 3 s.
# Uso: bash docs/testing/evidence/f7/03-idempotency-concurrency.sh > docs/testing/evidence/f7/03-idempotency-concurrency.log 2>&1
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
FAIL=0
expect() { if [ "$2" = "$3" ]; then echo "  [OK]    $1 (esperado '$2', obtido '$3')"; else echo "  [FALHA] $1 (esperado '$2', obtido '$3')"; FAIL=$((FAIL+1)); fi; }
RUN=$(date -u +%H%M%S)$RANDOM
# data propria (2024) ainda vazia na fonte
pickdate() { local d; while :; do d="2024-$(printf '%02d' $((RANDOM%12+1)))-$(printf '%02d' $((RANDOM%28+1)))"; [ "$($PSQL_T -c "select count(*) from transactions where merchant_id='$1' and effective_date='$d'")" = 0 ] && { echo "$d"; return; }; done; }
race() { F7_TOKEN="$1" F7_CA="$CA" F7_GW=127.0.0.1:8443 python -I docs/testing/evidence/f7/f7_race.py "${@:2}"; }
echo "# F7 03 - idempotencia e concorrencia - $(date -u +%FT%TZ) - run=$RUN"
mktokens; echo "tokens: A ${#TA} chars, B ${#TB} chars"

sec "1. Replay sequencial pelo gateway (T-03, RF-06): mesma chave + mesmo corpo => 201 identico + Idempotency-Replayed"
D1=$(pickdate "$A"); K="f7-replay-$RUN"; BODY='{"type":"CREDIT","amount":"64.80","effectiveDate":"'$D1'","description":"f7-replay-'$RUN'"}'
expect "1o POST" 201 "$(post "$A" "$TA" "$K" "$BODY")"; ID1=$(jfield id < "$B"); cp "$B" "$S/first.json"; echo "  Idempotency-Replayed no 1o: '$(grep -i '^idempotency-replayed' "$H" | tr -d '\r\n' || true)' (vazio esperado)"
expect "2o POST (replay)" 201 "$(post "$A" "$TA" "$K" "$BODY")"; echo "  cabecalho: $(grep -i '^idempotency-replayed' "$H" | tr -d '\r\n')"
expect "Idempotency-Replayed: true" "true" "$(grep -i '^idempotency-replayed' "$H" | tr -d '\r\n' | sed 's/.*: *//')"
expect "mesmo id no replay" "$ID1" "$(jfield id < "$B")"
echo "  igualdade de bytes 1o x replay: $(cmp -s "$S/first.json" "$B" && echo IGUAIS || echo 'DIFERENTES (item P2 jsonb conhecido: igualdade semantica)')"
python -I - "$S/first.json" "$B" <<'PY'
import json,sys
a,b=[json.load(open(p)) for p in sys.argv[1:3]]
print("  igualdade semantica (JSON parseado):", "IGUAIS" if a==b else "DIFERENTES", "| campos 1o:", sorted(a), "| replay:", sorted(b) if a!=b else "idem")
PY
echo "  mesma chave, corpo DIFERENTE (amount 99.00) => 422 (ADR-0005 item 37; 409 e so para concorrencia sem desfecho):"
C2=$(post "$A" "$TA" "$K" '{"type":"CREDIT","amount":"99.00","effectiveDate":"'$D1'","description":"f7-replay-'$RUN'"}'); echo "  status=$C2 corpo=$(head -c 200 "$B")"
expect "chave reutilizada com payload diferente" 422 "$C2"
expect "uma unica linha na fonte para a chave" 1 "$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and description='f7-replay-$RUN'")"
echo "  mesma chave na rota do merchant B (outra chave de escopo): $(post "$BM" "$TB" "$K" '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D1'"}') (201 esperado: escopo por merchant)"

sec "2. Corrida: 30 POSTs SIMULTANEOS, mesma chave e mesmo corpo (T-21) - oraculo: 1 lancamento, 1 evento na outbox"
sleep 3; D2=$(pickdate "$A"); L2="f7-race-same-$RUN"; K2="f7-key-$RUN"
OB0=$($PSQL_T -c "select count(*) from outbox_messages")
race "$TA" same_key "$A" "$D2" 30 "$K2" "$L2" | sed 's/^/  /'
expect "linhas na fonte com o rotulo" 1 "$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and description='$L2'")"
expect "linhas em idempotency_keys para a chave" 1 "$($PSQL_T -c "select count(*) from idempotency_keys where merchant_id='$A' and idempotency_key='$K2'" 2>&1 | tail -1)"
expect "eventos novos na outbox" 1 "$(( $($PSQL_T -c "select count(*) from outbox_messages") - OB0 ))"
converge "$A" "$TA" "$D2" 60

sec "3. Corrida: 20 POSTs simultaneos, mesma chave e corpos DIFERENTES - oraculo: exatamente 1 vence; os demais 422 (payload diferente da chave ja gravada)"
sleep 3; D3=$(pickdate "$A"); L3="f7-race-diff-$RUN"
race "$TA" same_key_diff "$A" "$D3" 20 "" "$L3" | sed 's/^/  /'
expect "linhas na fonte com o rotulo" 1 "$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and description='$L3'")"

sec "4. Lost update (T-06): 40 POSTs simultaneos de chaves distintas, mesmo merchant e mesma data - oraculo: fonte = 40,00 = projecao"
sleep 3; D4=$(pickdate "$A"); L4="f7-race-dist-$RUN"
race "$TA" distinct "$A" "$D4" 40 "" "$L4" | sed 's/^/  /'
echo "  fonte: $(src "$A" "$D4")  (creditos debitos contagem)"
OK4=$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and description='$L4'")
echo "  lancamentos criados: $OK4 (201 contados acima; 429 reduzem o numero criado e NAO sao perda: o cliente foi informado)"
converge "$A" "$TA" "$D4" 90
echo "  projecao (daily_consolidations): $(proj "$A" "$D4")"

sec "5. Estorno concorrente (RF-08, T-21): original criado; 20 estornos mesma chave; depois 20 estornos chaves diferentes"
sleep 3; D5=$(pickdate "$BM"); L5="f7-rev-$RUN"
expect "original" 201 "$(post "$BM" "$TB" "f7-orig-$RUN" '{"type":"CREDIT","amount":"100.00","effectiveDate":"'$D5'","description":"f7-orig-'$RUN'"}')"; ORIG=$(jfield id < "$B")
race "$TB" reversal_same "$BM" "$D5" 20 "$ORIG" "$L5" | sed 's/^/  mesma chave: /'
expect "estornos do original apos 20 mesma chave" 1 "$($PSQL_T -c "select count(*) from transactions where reversal_of='$ORIG'")"
sleep 3
race "$TB" reversal_diff "$BM" "$D5" 20 "$ORIG" "$L5" | sed 's/^/  chaves diferentes: /'
expect "estornos do original apos mais 20 chaves diferentes" 1 "$($PSQL_T -c "select count(*) from transactions where reversal_of='$ORIG'")"
echo "  fonte B/$D5: $(src "$BM" "$D5")"
converge "$BM" "$TB" "$D5" 90
echo "  consolidado B/$D5: $(daily "$BM" "$TB" "$D5")"

sec "6. Integridade global apos o roteiro: toda linha da fonte tem evento; nenhuma mensagem pendente; DLQ vazia"
sleep 5
echo "  outbox pendentes: $($PSQL_T -c 'select count(*) from outbox_messages where published_at is null')"
echo "  transactions sem evento correspondente na outbox: $($PSQL_T -c "select count(*) from transactions t where not exists (select 1 from outbox_messages o where o.aggregate_id = t.id)" 2>&1 | tail -1)"
qctl
echo; echo "RESULTADO: $FAIL verificacao(oes) com FALHA"; exit $FAIL
