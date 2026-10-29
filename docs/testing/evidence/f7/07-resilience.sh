#!/usr/bin/env bash
# F7 (T-03/T-04/T-05/T-10 + RNF-01): resiliencia na stack Compose BASE REAL, com carga de POST constante pelo gateway
# (f7_poster.py, uma Idempotency-Key unica por POST, registrada em poster-logs/<rotulo>.csv). Oraculos, por cenario:
#  (1) toda chave confirmada (201) existe em idempotency_keys/transactions (zero perda confirmada);
#  (2) linhas em transactions == chaves em idempotency_keys (zero duplicacao); (3) apos reenvio com a MESMA chave das
#  respostas nao-201 (cenarios com queda da API/gateway) nenhuma chave fica sem lancamento nem com dois;
#  (4) consolidado via gateway == soma da FONTE (transactions) e processed_messages == lancamentos (Inbox sem duplicata).
# NOTA (descoberta da 1a execucao, 07-resilience-run1-ABORTADO.log): `docker kill -s KILL` NAO aciona a politica de restart (o daemon marca
# a saida como solicitada); a QUEDA e simulada com SIGKILL no PID do processo principal, de fora do namespace (alpine --pid=host).
# Datas 2026-06-01..07 (sem dados previos; 2026-03-01..03, 2026-04-01..07 e 2026-05-01..07 contem massa das execucoes anteriores). Destrutivo para a stack (stop/kill de containers); a stack volta saudavel ao fim.
# Uso: bash 07-resilience.sh > 07-resilience.log 2>&1 ; echo "exit=$?" >> 07-resilience.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
D="$REPO/docs/testing/evidence/f7"; PL="$D/poster-logs"; mkdir -p "$PL"
win() { command -v cygpath >/dev/null 2>&1 && cygpath -m "$1" || echo "$1"; }
CA_WIN="$PKI_WIN/ca.crt"
FAIL=0
# Datas DR* sobrescreviveis por ambiente: os oraculos de contagem (cenario 5) exigem datas sem lancamentos de rodadas anteriores.
RUN=$(date +%s)  # sufixo unico: a mesma chave com outro payload retorna 422 (ADR-0005), entao chaves fixas quebram a reexecucao
expect() { if [ "$2" = "$3" ]; then echo "  OK: $1 = $3"; else echo "  FALHA: $1 esperado='$2' obtido='$3'"; FAIL=$((FAIL+1)); fi; }
poster() { # poster <merchant> <client_id> <secret> <date> <req/s> <segundos> <rotulo>
  F7_SECRET="$3" F7_CA="$CA_WIN" F7_GW=127.0.0.1:8443 F7_KC=127.0.0.1:8080 python -I "$(win "$D/f7_poster.py")" "$1" "$2" "$4" "$5" "$6" "$7" "$(win "$PL/$7.csv")"
}
retry() { # retry <merchant> <client_id> <secret> <date> <rotulo>
  F7_SECRET="$3" F7_CA="$CA_WIN" F7_GW=127.0.0.1:8443 F7_KC=127.0.0.1:8080 python -I "$(win "$D/f7_retry.py")" "$1" "$2" "$4" "$5" "$(win "$PL/$5.csv")"
}
outbox_pend() { $PSQL_T -c "SELECT count(*) FILTER (WHERE published_at IS NULL) FROM outbox_messages"; }
wait_outbox0() { local i p; for i in $(seq 1 "${1:-90}"); do p=$(outbox_pend); [ "$p" = 0 ] && break; sleep 1; done; echo "[$(ts)] outbox pendentes=$p apos ${i}s"; }
pre_retry() { # pre_retry <rotulo> <merchant>: ANTES do retry, quantas chaves nao-201 do poster ja existem no banco (falha ambigua demonstrada)
  local l="$1" m="$2"
  grep -E '^[0-9]+,' "$PL/$l.csv" | tr -d '' | awk -F, '$3!=201{print $2}' | sort -u > "$S/pre_non201"
  $PSQL_T -c "select idempotency_key from idempotency_keys where merchant_id='$m' and idempotency_key like '$l-%'" | tr -d '' | sort -u > "$S/pre_db"
  echo "  [pre-retry $l] nao-201=$(wc -l < "$S/pre_non201") | ja gravadas no banco ANTES do retry=$(comm -12 "$S/pre_non201" "$S/pre_db" | wc -l) | ausentes no banco antes do retry=$(comm -23 "$S/pre_non201" "$S/pre_db" | wc -l)"
}
oracle() { # oracle <rotulo> <merchant> <date> <token_consolidation_read> [apos_retry]
  local l="$1" m="$2" d="$3" t="$4"
  grep -E '^[0-9]+,' "$PL/$l.csv" | tr -d '\r' > "$S/log.csv"
  awk -F, '{print $2}' "$S/log.csv" | sort -u > "$S/k_all"
  awk -F, '$3==201{print $2}' "$S/log.csv" | sort -u > "$S/k_201"
  awk -F, '$3!=201{print $2}' "$S/log.csv" | sort -u > "$S/k_non201"
  $PSQL_T -c "select idempotency_key from idempotency_keys where merchant_id='$m' and idempotency_key like '$l-%'" | tr -d '\r' | sort -u > "$S/k_db"
  local n201 nnon dbk rows miss amb extra
  n201=$(wc -l < "$S/k_201"); nnon=$(wc -l < "$S/k_non201")
  dbk=$(comm -12 "$S/k_all" "$S/k_db" | wc -l)          # chaves DESTE poster presentes no banco
  extra=$(comm -13 "$S/k_all" "$S/k_db" | wc -l)        # chaves com o mesmo prefixo de execucoes anteriores (ignoradas)
  rows=$($PSQL_T -c "select count(*) from transactions where merchant_id='$m' and description='$l' and effective_date='$d'")
  miss=$(comm -23 "$S/k_201" "$S/k_db" | wc -l); amb=$(comm -12 "$S/k_non201" "$S/k_db" | wc -l)
  echo "  [oraculo $l $d] requisicoes=$(wc -l < "$S/k_all") 201=$n201 nao-201=$nnon | chaves do poster no banco=$dbk | linhas em transactions (merchant+rotulo+data)=$rows | nao-201 cuja chave existe no banco APOS o retry (inclui as criadas pelo proprio retry; NAO prova gravacao previa; ver pre_retry)=$amb | chaves de execucoes anteriores com o mesmo prefixo (ignoradas)=$extra"
  expect "chaves confirmadas (201) ausentes no banco (perda)" 0 "$miss"
  expect "linhas em transactions (merchant+rotulo+data) == chaves do poster no banco (duplicacao)" "$dbk" "$rows"
  if [ "${5:-}" = apos_retry ]; then
    expect "chaves do poster sem lancamento apos retry com a mesma chave" 0 "$(comm -23 "$S/k_all" "$S/k_db" | wc -l)"
    expect "linhas na fonte == chaves distintas enviadas" "$(wc -l < "$S/k_all")" "$rows"
  fi
  local out; out=$(converge "$m" "$t" "$d" 120); echo "  $out"
  expect "consolidado (gateway) == fonte" IGUAL "$(echo "$out" | grep -oE 'IGUAL|DIVERGENTE' | tail -1)"
  local cnt; cnt=$(src "$m" "$d" | awk '{print $3}')
  expect "processed_messages (Inbox) == lancamentos da data, sem duplicata" "$cnt $cnt" "$($PSQL_C -F ' ' -c "select count(*), count(distinct transaction_id) from processed_messages where merchant_id='$m' and effective_date='$d'")"
}
watch_restart() { # watch_restart <container> <t0_ns> <limite s>: registra transicoes de estado e tempos ate running e healthy
  local c="$1" t0="$2" max="$3" st last="" tr="" th="" i
  for i in $(seq 1 $(( max * 2 ))); do
    st=$(docker inspect -f '{{.State.Status}}/{{.State.Health.Status}}/rc={{.RestartCount}}' "$c" 2>/dev/null)
    if [ "$st" != "$last" ]; then echo "  [+$(( ($(date +%s%N)-t0)/1000000 )) ms] estado=$st"; last="$st"; fi
    case "$st" in running/*) [ -z "$tr" ] && tr=$(( ($(date +%s%N)-t0)/1000000 ));; esac
    case "$st" in running/healthy/*) th=$(( ($(date +%s%N)-t0)/1000000 )); break;; esac; sleep 0.5
  done
  echo "  tempo ate running: ${tr:-NUNCA} ms | ate healthy: ${th:-NUNCA} ms (limite ${max}s, granularidade ~0,5 s + custo do docker inspect) | ExitCode ATUAL (lido apos o restart: e o do processo NOVO, nao o do morto): $(docker inspect -f '{{.State.ExitCode}}' "$c")"
  [ -n "$th" ]
}
crash() { # crash <container>: SIGKILL no PID do processo principal visto pelo daemon (de FORA do namespace do container); o daemon
  # trata como queda do processo (NAO como 'docker kill'), entao a politica restart: unless-stopped deve agir.
  local c="$1" p t0
  echo "politica de restart de $c: $(docker inspect -f '{{.HostConfig.RestartPolicy.Name}}' "$c") | RestartCount antes: $(docker inspect -f '{{.RestartCount}}' "$c")"
  p=$(docker inspect -f '{{.State.Pid}}' "$c"); t0=$(date +%s%N)
  MSYS_NO_PATHCONV=1 docker run --rm --pid=host alpine:latest kill -9 "$p" >/dev/null 2>&1; echo "[$(ts)] kill -9 do PID $p (container $c) via container efemero --pid=host"
  watch_restart "$c" "$t0" 90
}

echo "# F7 07 - resiliencia - $(date -u +%FT%TZ)"
echo "estado inicial: $($C ps --format '{{.Name}}={{.Health}}' | tr '\n' ' ')"
mktokens
SA="$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET"
echo "NOTA: f7_poster.py abre UMA conexao HTTPS nova por requisicao (inclui handshake TLS); suas latencias NAO sao comparaveis com as do k6 (keep-alive)."
echo "pre-checagem (linhas por data, merchant A, esperado 0 antes do script): $(for d in ${DR1:-2026-06-01} ${DR2:-2026-06-02} ${DR3:-2026-06-03} ${DR4:-2026-06-04} ${DR5:-2026-06-05} ${DR4B:-2026-06-06} ${DR4C:-2026-06-07}; do echo -n "$d=$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and effective_date='$d'") "; done)"
echo "healthcheck presente nos alvos de kill: $(for c in cashflow-consolidation-worker-1 cashflow-transactions-api-1 cashflow-gateway-1 cashflow-consolidation-api-1; do echo -n "$c=$(docker inspect -f '{{if .State.Health}}sim{{else}}NAO{{end}}' $c) "; done)"

sec "R0. 'docker kill' NAO aciona restart: unless-stopped (observacao empirica, worker ocioso)"
echo "RestartCount antes do kill: $(docker inspect -f '{{.RestartCount}}' cashflow-consolidation-worker-1) (restarts automaticos de execucoes anteriores persistem ate um start manual)"
t0=$(date +%s%N); docker kill -s KILL cashflow-consolidation-worker-1 >/dev/null; echo "[$(ts)] docker kill -s KILL cashflow-consolidation-worker-1"
sleep 20; echo "20 s depois: $(docker inspect -f '{{.State.Status}} exit={{.State.ExitCode}} rc={{.RestartCount}}' cashflow-consolidation-worker-1)"
expect "docker kill: o container continua exited (sem restart automatico)" exited "$(docker inspect -f '{{.State.Status}}' cashflow-consolidation-worker-1)"
docker start cashflow-consolidation-worker-1 >/dev/null; wait_healthy cashflow-consolidation-worker-1 120

sec "R1. worker E consolidation-api parados; POSTs continuam (RNF-01); religa -> convergencia"
DR1=${DR1:-2026-06-01}
$C stop consolidation-worker consolidation-api 2>&1 | tail -2
sleep 15; echo "apos 15 s parados (docker stop manual NAO deve reiniciar): $($C ps -a --format '{{.Name}}={{.State}}' | grep -E 'consolidation' | tr '\n' ' ')"
req "GET consolidado pelo gateway com consolidation-api parada (captura de 5xx real: Server/headers)" "$GW/merchants/$A/consolidations/daily?date=$DR1" -H "Authorization: Bearer $TA"
poster "$A" merchant-a-system "$SA" "$DR1" 10 20 f7-r1 > "$S/p.out"; cat "$S/p.out"
qctl
echo "outbox pendentes (broker no ar: esperado ~0, mensagens enfileiradas): $(outbox_pend)"
echo "--- headers de uma mensagem enfileirada (peek, requeue) - ha traceparent? (item F7 herdado)"
mq -X POST "$MQ/queues/%2F/consolidation.transaction-registered/get" -d '{"count":1,"ackmode":"ack_requeue_true","encoding":"auto","truncate":50}' | python -I -c '
import json,sys
for m in json.load(sys.stdin): print("  chaves de properties.headers:", sorted(m["properties"].get("headers",{}).keys()), "| message_id presente:", bool(m["properties"].get("message_id")))'
T0=$(date +%s)
$C start consolidation-worker consolidation-api 2>&1 | tail -2
wait_healthy cashflow-consolidation-worker-1 120; wait_healthy cashflow-consolidation-api-1 120
mktokens; oracle f7-r1 "$A" "$DR1" "$TA"; echo "tempo desde o start ate o fim da verificacao: $(( $(date +%s)-T0 )) s"
expect "POSTs nao-201 com worker+consolidation-api parados" 0 "$(awk -F, '$3!=201' "$PL/f7-r1.csv" | wc -l)"
qctl

sec "R2. rabbitmq PARADO durante rajada de POST (10/s por 30 s; para em +8 s, volta em ~+20 s) -> outbox entrega depois"
DR2=${DR2:-2026-06-02}
poster "$A" merchant-a-system "$SA" "$DR2" 10 30 f7-r2 > "$S/p.out" & PP=$!
sleep 8; $C stop rabbitmq 2>&1 | tail -1; echo "[$(ts)] rabbitmq parado"
sleep 6; echo "outbox pendentes durante a queda: $(outbox_pend) | transactions-api health=$(docker inspect -f '{{.State.Health.Status}}' cashflow-transactions-api-1)"
$C start rabbitmq 2>&1 | tail -1; echo "[$(ts)] rabbitmq religado"
wait $PP; cat "$S/p.out"
wait_healthy cashflow-rabbitmq-1 120; wait_outbox0 120
echo "tentativas maximas na outbox: $($PSQL_T -c "select coalesce(max(attempts),0) from outbox_messages")"
mktokens; oracle f7-r2 "$A" "$DR2" "$TA"
expect "POSTs nao-201 durante a queda do broker" 0 "$(awk -F, '$3!=201' "$PL/f7-r2.csv" | wc -l)"
qctl

sec "R3. QUEDA (SIGKILL no PID) do WORKER durante rajada de POST (10/s por 25 s; kill em +6 s)"
DR3=${DR3:-2026-06-03}
poster "$A" merchant-a-system "$SA" "$DR3" 10 25 f7-r3 > "$S/p.out" & PP=$!
sleep 6; crash cashflow-consolidation-worker-1; expect "worker reiniciado sozinho (running/healthy)" 0 $?
wait $PP; cat "$S/p.out"
wait_outbox0 90; mktokens; oracle f7-r3 "$A" "$DR3" "$TA"
expect "POSTs nao-201 com o worker morto" 0 "$(awk -F, '$3!=201' "$PL/f7-r3.csv" | wc -l)"

sec "R4a. QUEDA (SIGKILL no PID) da TRANSACTIONS-API durante rajada de POST (10/s por 30 s; kill em +6 s) + retry com a MESMA chave"
DR4=${DR4:-2026-06-04}
poster "$A" merchant-a-system "$SA" "$DR4" 10 30 f7-r4a > "$S/p.out" & PP=$!
sleep 6; crash cashflow-transactions-api-1; expect "transactions-api reiniciada sozinha" 0 $?
wait $PP; cat "$S/p.out"
echo "status das requisicoes nao-201 (rotulo f7-r4a): $(awk -F, '$3!=201{print $3}' "$PL/f7-r4a.csv" | sort | uniq -c | tr '\n' ';')"
pre_retry f7-r4a "$A"
mktokens; retry "$A" merchant-a-system "$SA" "$DR4" f7-r4a
wait_outbox0 90; oracle f7-r4a "$A" "$DR4" "$TA" apos_retry

sec "R4b. QUEDA (SIGKILL no PID) do GATEWAY durante rajada de POST (10/s por 25 s; kill em +6 s) + retry com a MESMA chave"
DR4B=${DR4B:-2026-06-06}
poster "$A" merchant-a-system "$SA" "$DR4B" 10 25 f7-r4b > "$S/p.out" & PP=$!
sleep 6; crash cashflow-gateway-1; expect "gateway reiniciado sozinho" 0 $?
wait $PP; cat "$S/p.out"
echo "status das requisicoes nao-201 (rotulo f7-r4b): $(awk -F, '$3!=201{print $3}' "$PL/f7-r4b.csv" | sort | uniq -c | tr '\n' ';')"
pre_retry f7-r4b "$A"
mktokens; retry "$A" merchant-a-system "$SA" "$DR4B" f7-r4b
wait_outbox0 90; oracle f7-r4b "$A" "$DR4B" "$TA" apos_retry

sec "R4c. QUEDA (SIGKILL no PID) da CONSOLIDATION-API: GET pelo gateway ate voltar 200; POST segue funcionando"
DR4C=${DR4C:-2026-06-07}
mktokens
t0=$(date +%s%N); P=$(docker inspect -f '{{.State.Pid}}' cashflow-consolidation-api-1); MSYS_NO_PATHCONV=1 docker run --rm --pid=host alpine:latest kill -9 "$P" >/dev/null 2>&1; echo "[$(ts)] kill -9 do PID $P (consolidation-api)"
prev=""; for i in $(seq 1 300); do
  c1=$(code "$GW/merchants/$A/consolidations/daily?date=$DR4" -H "Authorization: Bearer $TA")
  if [ "$c1" != "$prev" ]; then echo "  [+$(( ($(date +%s%N)-t0)/1000000 )) ms] GET consolidado via gateway -> $c1"; prev="$c1"; fi
  [ "$c1" = 200 ] && break; sleep 0.2; done
echo "  restart policy: $(docker inspect -f '{{.HostConfig.RestartPolicy.Name}} rc={{.RestartCount}} {{.State.Status}}/{{.State.Health.Status}}' cashflow-consolidation-api-1)"
expect "POST durante/apos a queda da consolidation-api" 201 "$(post "$A" "$TAW" f7-r4c-1-$RUN '{"type":"CREDIT","amount":"7.00","effectiveDate":"'$DR4C'","description":"f7-r4c"}')"
wait_healthy cashflow-consolidation-api-1 60; mktokens
out=$(converge "$A" "$TA" "$DR4C" 60); echo "  $out"; expect "consolidado == fonte apos o restart" IGUAL "$(echo "$out" | grep -oE 'IGUAL|DIVERGENTE' | tail -1)"

sec "R5. lock de linha de 9 s em daily_consolidations(A) (PROXY do recon segurando o lock; NAO e o recon-tool): B fica bloqueado? (worker com ConsumerDispatchConcurrency=1)"
DR5=${DR5:-2026-06-05}
mktokens
TBW=$(token merchant-b-system "$KEYCLOAK_MERCHANT_B_SYSTEM_SECRET" transactions.write)
echo "semente: 1 lancamento por merchant em $DR5"
echo "  A -> $(post "$A" "$TAW" f7-r5-seedA-$RUN '{"type":"CREDIT","amount":"10.00","effectiveDate":"'$DR5'","description":"f7-r5"}')  B -> $(post "$BM" "$TBW" f7-r5-seedB-$RUN '{"type":"CREDIT","amount":"20.00","effectiveDate":"'$DR5'","description":"f7-r5"}')"
mktokens; converge "$A" "$TA" "$DR5" 30; converge "$BM" "$TB" "$DR5" 30
SINCE=$(date -u +%FT%TZ)
( docker exec -i cashflow-postgres-1 psql -U migrator -d consolidation_db -v ON_ERROR_STOP=1 -At -c "BEGIN; SELECT 'lock tomado' FROM daily_consolidations WHERE merchant_id='$A' AND effective_date='$DR5' FOR UPDATE; SELECT pg_sleep(9); COMMIT;" > "$S/lock.out" 2>&1 ) &
LP=$!; sleep 1.5; TL=$(date +%s%N)
wait_cnt() { # wait_cnt <merchant> <contagem esperada> <t0_ns> -> ms ate a projecao atingir a contagem (ou vazio)
  local i; for i in $(seq 1 120); do [ "$(proj "$1" "$DR5" | awk '{print $3}')" = "$2" ] && { echo $(( ($(date +%s%N)-$3)/1000000 )); return; }; sleep 0.25; done; }
echo "--- (a) lock mantido sobre A; chega SO um evento de B (nenhum de A na fila)"
TB1=$(date +%s%N); PB1=$(post "$BM" "$TBW" f7-r5-B2-$RUN '{"type":"CREDIT","amount":"5.00","effectiveDate":"'$DR5'","description":"f7-r5"}')
B1=$(wait_cnt "$BM" 2 "$TB1"); echo "  POST B=$PB1; projecao de B reflete o 2o lancamento em ${B1:-nao convergiu} ms (lock de A ainda ativo: $(( 9000 - ($(date +%s%N)-TL)/1000000 )) ms restantes aprox.)"
echo "--- (b) AINDA sob o lock: chega um evento de A e, logo depois, outro de B"
TA1=$(date +%s%N); PA=$(post "$A" "$TAW" f7-r5-A2-$RUN '{"type":"CREDIT","amount":"5.00","effectiveDate":"'$DR5'","description":"f7-r5"}')
PB2=$(post "$BM" "$TBW" f7-r5-B3-$RUN '{"type":"CREDIT","amount":"5.00","effectiveDate":"'$DR5'","description":"f7-r5"}')
echo "  [$(ts)] POST A=$PA POST B=$PB2"
bl=""; al=""
for i in $(seq 1 400); do
  [ -z "$bl" ] && [ "$(proj "$BM" "$DR5" | awk '{print $3}')" = 3 ] && bl=$(( ($(date +%s%N)-TA1)/1000000 ))
  [ -z "$al" ] && [ "$(proj "$A" "$DR5" | awk '{print $3}')" = 2 ] && al=$(( ($(date +%s%N)-TA1)/1000000 ))
  [ -n "$bl" ] && [ -n "$al" ] && break; sleep 0.25; done
wait $LP; tr '
' ' ' < "$S/lock.out"; echo
echo "  tempo, contado a partir do POST de A em (b): evento de A (travado no lock)=${al:-nao convergiu} ms | evento de B posterior=${bl:-nao convergiu} ms"
echo "  worker: linhas com 55P03/lock desde $SINCE: $(docker logs --since "$SINCE" cashflow-consolidation-worker-1 2>&1 | grep -ciE '55P03|lock')"
docker logs --since "$SINCE" cashflow-consolidation-worker-1 2>&1 | grep -iE '55P03|lock' | head -3 | cut -c1-300 | sed 's/^/    amostra: /'
mktokens
expect "A convergiu depois que o lock foi liberado" IGUAL "$(converge "$A" "$TA" "$DR5" 60 | grep -oE 'IGUAL|DIVERGENTE' | tail -1)"
expect "B convergiu" IGUAL "$(converge "$BM" "$TB" "$DR5" 60 | grep -oE 'IGUAL|DIVERGENTE' | tail -1)"

sec "estado final"
qctl
$C ps --format 'table {{.Name}}\t{{.Status}}'
echo "nao saudaveis: $($C ps --format '{{.Name}}={{.Health}}' | grep -v '=healthy' | tr '\n' ' ')"
echo "FALHAS=$FAIL"
[ "$FAIL" = 0 ]
