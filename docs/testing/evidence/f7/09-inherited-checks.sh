#!/usr/bin/env bash
# F7: itens herdados de decisions-needed.md (linhas ~105-145) que SAO testaveis contra a stack real sem editar src/infra.
#  I1 pageSize fora de [1,200] -> 422; Cache-Control nas respostas de erro (item "no-store so no 200")
#  I2 privilegios de tx_app/cons_worker/recon_* (item "tx_app com UPDATE/DELETE amplos")
#  I3 recon-tool: rebuild de data SEM lancamentos na fonte; argumentos invalidos (exit 64); intervalo > 366 dias
#  I4 recon-tool: lock de linha mantido por 9 s por terceiro -> recon deve abortar com lock_timeout (exit/linha de execucao)
#  I5 limitador anonimo por IP atras do NAT do Docker: requisicoes do host (IP do NAT) x de um container (IP proprio)
#  I6 retencao de idempotency_keys: expires_at das chaves recentes (o expurgo NAO e testavel sem alterar src/infra)
# Uso: bash 09-inherited-checks.sh > 09-inherited-checks.log 2>&1 ; echo "exit=$?" >> 09-inherited-checks.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
RECON="$C --profile tools run --rm recon-tool"
FAIL=0
expect() { if [ "$2" = "$3" ]; then echo "  OK: $1 = $3"; else echo "  FALHA: $1 esperado='$2' obtido='$3'"; FAIL=$((FAIL+1)); fi; }
echo "# F7 09 - itens herdados testaveis - $(date -u +%FT%TZ)"
mktokens

sec "I1. pageSize e Cache-Control nas respostas de erro (via gateway)"
for ps in 200 201 0 abc; do
  echo "pageSize=$ps -> $(code "$GW/merchants/$A/transactions?from=2026-02-10&to=2026-02-10&pageSize=$ps" -H "Authorization: Bearer $TA")"
done
expect "pageSize=200 aceito" 200 "$(code "$GW/merchants/$A/transactions?from=2026-02-10&to=2026-02-10&pageSize=200" -H "Authorization: Bearer $TA")"
expect "pageSize=201 rejeitado" 422 "$(code "$GW/merchants/$A/transactions?from=2026-02-10&to=2026-02-10&pageSize=201" -H "Authorization: Bearer $TA")"
req "422 (pageSize=201): cabecalhos de cache" "$GW/merchants/$A/transactions?from=2026-02-10&to=2026-02-10&pageSize=201" -H "Authorization: Bearer $TA"
req "404 de recurso inexistente da API (via gateway)" "$GW/merchants/$A/transactions/99999999-9999-4999-8999-999999999999" -H "Authorization: Bearer $TA"
req "200 de referencia (consolidado)" "$GW/merchants/$A/consolidations/daily?date=2026-02-10" -H "Authorization: Bearer $TA"
req "401 sem token" "$GW/merchants/$A/consolidations/daily?date=2026-02-10"

sec "I2. privilegios por papel (information_schema, como postgres)"
for db in transactions_db consolidation_db; do
  echo "--- $db"
  docker exec -i cashflow-postgres-1 psql -U postgres -d "$db" -At -c "select grantee||' '||table_name||' '||string_agg(privilege_type,',' order by privilege_type) from information_schema.role_table_grants where grantee in ('tx_app','cons_ro','cons_worker','recon_ro','recon_writer') group by grantee,table_name order by 1"
  echo "  colunas com grant proprio:"; docker exec -i cashflow-postgres-1 psql -U postgres -d "$db" -At -c "select grantee||' '||table_name||'.'||column_name||' '||privilege_type from information_schema.role_column_grants where grantee in ('tx_app','cons_ro','cons_worker','recon_ro','recon_writer') and table_schema='public' order by 1" | sed 's/^/    /'
done
expect "tx_app NAO tem UPDATE em transactions" f "$(docker exec -i cashflow-postgres-1 psql -U postgres -d transactions_db -At -c "select has_table_privilege('tx_app','transactions','UPDATE')")"
expect "tx_app NAO tem DELETE em transactions" f "$(docker exec -i cashflow-postgres-1 psql -U postgres -d transactions_db -At -c "select has_table_privilege('tx_app','transactions','DELETE')")"
echo "tx_app UPDATE/DELETE em idempotency_keys: $(docker exec -i cashflow-postgres-1 psql -U postgres -d transactions_db -At -c "select has_table_privilege('tx_app','idempotency_keys','UPDATE'), has_table_privilege('tx_app','idempotency_keys','DELETE')") (esperado t,t para o expurgo por expires_at)"

sec "I3. recon-tool: casos de borda"
echo "--- rebuild de data sem lancamentos na fonte (2031-01-01)"
$RECON rebuild --merchant "$A" --from 2031-01-01 --to 2031-01-01 2>&1 | tail -8; echo "rebuild vazio exit=${PIPESTATUS[0]}"
echo "--- reconcile com from > to"
$RECON reconcile --merchant "$A" --from 2026-03-02 --to 2026-03-01 2>&1 | tail -4; echo "exit=${PIPESTATUS[0]}"
echo "--- intervalo de 400 dias (teto 366)"
$RECON reconcile --merchant "$A" --from 2025-01-01 --to 2026-02-05 2>&1 | tail -4; echo "exit=${PIPESTATUS[0]}"
echo "--- comando desconhecido"
$RECON frobnicate 2>&1 | tail -4; echo "exit=${PIPESTATUS[0]}"

sec "I4. recon-tool x lock de linha mantido por 9 s (data 2026-06-05, A; massa criada no 07)"
if [ "$($PSQL_C -c "select count(*) from daily_consolidations where merchant_id='$A' and effective_date='2026-06-05'")" = 1 ]; then
  ( docker exec -i cashflow-postgres-1 psql -U migrator -d consolidation_db -v ON_ERROR_STOP=1 -At -c "BEGIN; SELECT 'lock tomado' FROM daily_consolidations WHERE merchant_id='$A' AND effective_date='2026-06-05' FOR UPDATE; SELECT pg_sleep(9); COMMIT;" > "$S/lock.out" 2>&1 ) &
  LP=$!; sleep 1.5; T0=$(date +%s%N)
  $RECON reconcile --merchant "$A" --from 2026-06-05 --to 2026-06-05 2>&1 | tail -10; echo "recon exit=${PIPESTATUS[0]} apos $(( ($(date +%s%N)-T0)/1000000 )) ms"
  wait $LP
  echo "--- mesma reconciliacao sem o lock"
  $RECON reconcile --merchant "$A" --from 2026-06-05 --to 2026-06-05 2>&1 | tail -6; echo "recon exit=${PIPESTATUS[0]}"
else echo "PULADO: linha 2026-06-05 de A inexistente (rode 07-resilience.sh antes)"; fi

sec "I5. limitador anonimo por IP atras do NAT do Docker (20 req/s por IP, HIPOTESE C-F5-08)"
burst_host() { # 60 requisicoes anonimas quase simultaneas do HOST; imprime distribuicao de status
  for i in $(seq 1 60); do ( c=$(code "$GW/merchants/$A/consolidations/daily?date=2026-02-10"); echo "$c" ) & done 2>/dev/null | sort | uniq -c | tr '
' ' '; wait; }
echo "host (IP de origem visto pelo gateway = NAT do Docker Desktop) 60 anonimas: $(burst_host)"
sleep 3
echo "container na rede cashflow_app (IP proprio) 60 anonimas, em paralelo com outra rajada do host:"
( burst_host > "$S/host2.out" 2>&1 ) &
HB=$!
MSYS_NO_PATHCONV=1 docker run --rm --network cashflow_app -v "$PKI_WIN/ca.crt:/certs/ca.crt:ro" --entrypoint sh "$CURL_IMG" -c '
for i in $(seq 1 60); do curl -s -o /dev/null -w "%{http_code}\n" --cacert /certs/ca.crt "https://gateway:8080/api/v1/merchants/'"$A"'/consolidations/daily?date=2026-02-10" & done | sort | uniq -c | tr "\n" " "; wait' 2>&1 | sed 's/^/  container: /'
wait $HB; echo "  host (simultaneo): $(cat "$S/host2.out")"
echo "leitura: 429 em requisicoes SEM token = o limitador por IP atua ANTES da autenticacao (401); contagens por status acima"

sec "I6. idempotency_keys: expires_at (retencao)"
$PSQL_T -F ' ' -c "select count(*), min(expires_at - created_at), max(expires_at - created_at), count(*) filter (where expires_at < now()) from idempotency_keys"
echo "(o job de expurgo, se existir, nao e exercitado aqui: depende de codigo/infra fora do escopo de F7)"

sec "estado final"
$C ps --format 'table {{.Name}}\t{{.Status}}'
echo "FALHAS=$FAIL"
[ "$FAIL" = 0 ]
