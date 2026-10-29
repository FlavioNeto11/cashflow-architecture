#!/usr/bin/env bash
# F7 (T-08): executa UMA rodada do k6 (imagem fixada por digest) contra o gateway real, dentro da rede Compose `cashflow_app`
# (gateway:8080 com TLS de borda da CA local via SSL_CERT_FILE; Keycloak em keycloak:8080, o issuer do Compose).
# C-25: monta SOMENTE ca.crt (nenhuma chave privada no container do k6). O k6 VERIFICA o TLS de borda: SSL_CERT_FILE=/certs/ca.crt e insecureSkipTLSVerify=false (f7-t08.js).
# Uso: bash 05-k6-run.sh <label> [VAR=valor ...]   (VARs repassadas ao f7-t08.js: TOKENS, GET_RATE, POST_RATE, DURATION_S, ...)
# O oraculo (EXPECT) vem do PostgreSQL no inicio da rodada, nao de constantes. Saidas: k6/<label>.json (resumo) e stdout.
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
K6_IMG=grafana/k6@sha256:3e4186aea62e11a45560bfc51e19eeafe82fc07ce069c78aff44b7ff893d9a5e
LABEL="${1:?label}"; shift
DL=2026-02-10
OUT_DIR="$REPO/docs/testing/evidence/f7/k6"; mkdir -p "$OUT_DIR"
win() { command -v cygpath >/dev/null 2>&1 && cygpath -m "$1" || echo "$1"; }
exp() { # exp <merchant>: JSON do oraculo para a data DL a partir da FONTE (transactions)
  read -r cr db n < <(src "$1" "$DL")
  local closing; closing=$($PSQL_T -c "SELECT (coalesce(sum(CASE WHEN entry_type=1 THEN amount ELSE -amount END),0))::numeric(24,2) FROM transactions WHERE merchant_id='$1' AND effective_date<='$DL'")
  python -I -c 'import json,sys; cr,db,n,cl=sys.argv[1:5]; from decimal import Decimal as D; print(json.dumps({"credits":cr,"debits":db,"net":str(D(cr)-D(db)),"count":int(n),"closing":cl}))' "$cr" "$db" "$n" "$closing"
}
EXPECT="{\"A\":$(exp "$A"),\"B\":$(exp "$BM")}"
RUNID=$(date -u +%H%M%S)
echo "# F7 05 - k6 '$LABEL' - $(date -u +%FT%TZ)"
echo "oraculo (fonte PostgreSQL, data $DL): $EXPECT"
echo "imagem: $K6_IMG | rede: cashflow_app | alvo: https://gateway:8080 (TLS de borda, CA local via SSL_CERT_FILE)"
echo "estado antes: $($C ps --format '{{.Name}}={{.Health}}' | tr '\n' ' ')"
ENVARGS=(); for kv in "$@"; do ENVARGS+=(-e "$kv"); done
# amostras de CPU/memoria de TODOS os containers em execucao (inclui k6, worker e rabbitmq) durante a rodada. Passo B: laco continuo
# ate o fim da rodada (cada `docker stats --no-stream` leva ~2 s; intervalo efetivo ~3 a 5 s). STATS_OUT (opcional) grava o bruto.
STATS_OUT="${STATS_OUT:-$S/stats.txt}"
( while true; do echo "[stats $(date -u +%H:%M:%S)] $(docker stats --no-stream --format '{{.Name}} cpu={{.CPUPerc}} mem={{.MemUsage}}' | sed 's/^cashflow-//; s/-1 / /' | tr '\n' ';')"; sleep 1; done ) > "$STATS_OUT" 2>&1 &
SP=$!
MSYS_NO_PATHCONV=1 docker run --rm --name "f7-k6-$LABEL" --network cashflow_app \
  -e SSL_CERT_FILE=/certs/ca.crt -e KEYCLOAK_MERCHANT_A_SYSTEM_SECRET -e KEYCLOAK_MERCHANT_B_SYSTEM_SECRET \
  -e "LABEL=$LABEL" -e "DATE=$DL" -e "EXPECT=$EXPECT" -e "RUN_ID=$RUNID" "${ENVARGS[@]}" \
  -v "$(win "$REPO/docs/testing/evidence/f7"):/scripts:ro" -v "$(win "$PKI_WIN/ca.crt"):/certs/ca.crt:ro" -v "$(win "$OUT_DIR"):/out" \
  "$K6_IMG" run --quiet --no-usage-report /scripts/f7-t08.js 2>&1 | grep -vE '^\s*$'
RC=${PIPESTATUS[0]}
kill $SP 2>/dev/null; wait $SP 2>/dev/null
echo "--- amostras docker stats durante a rodada (bruto em $STATS_OUT)"; cut -c1-1200 "$STATS_OUT"
echo "estado depois: $($C ps --format '{{.Name}}={{.Health}}' | tr '\n' ' ')"
if [ -f "$OUT_DIR/$LABEL.json" ] && grep -q '"post": {' "$OUT_DIR/$LABEL.json"; then
  N201=$(python -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["post"]["r201_all_phases"])' "$OUT_DIR/$LABEL.json")
  DBN=$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and description='f7-k6-$RUNID'")
  echo "oraculo POST: 201 contados pelo k6 (todas as fases)=$N201 | linhas na fonte com description f7-k6-$RUNID=$DBN -> $([ "$N201" = "$DBN" ] && echo IGUAL || echo DIVERGENTE)"
  TA_ORACLE=$(token merchant-a-system "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" "consolidation.read")
  converge "$A" "$TA_ORACLE" "${POST_DATE_ORACLE:-2026-02-11}" 60
fi
echo "k6 rc=$RC"
exit $RC
