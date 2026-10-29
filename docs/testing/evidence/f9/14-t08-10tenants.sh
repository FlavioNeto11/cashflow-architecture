#!/usr/bin/env bash
# F9 (item 4 da auditoria final; T-08/T-30 com o perfil da ADR-0009): 50 req/s no consolidado diario, round-robin entre 10 merchants
# (5 req/s cada), 20 s de aquecimento e 120 s de medicao, pelo gateway real com TLS de borda verificado e JWT real por merchant.
# Merchants 0 e 1 sao A e B do realm; 2..9 usam clients TEMPORARIOS (f9-kc.sh), removidos no fim (trap), com massa propria
# criada pelo POST real e oraculo tirado do PostgreSQL. Amostras de docker stats durante a rodada (item 20) em 14-stats.txt.
# Uso: bash docs/testing/evidence/f9/14-t08-10tenants.sh > docs/testing/evidence/f9/14-t08-10tenants.log 2>&1
. "$(dirname "${BASH_SOURCE[0]}")/../f7/f7-common.sh"
E9="$REPO/docs/testing/evidence/f9"; . "$E9/f9-kc.sh"
K6_IMG=grafana/k6@sha256:3e4186aea62e11a45560bfc51e19eeafe82fc07ce069c78aff44b7ff893d9a5e
DL=2026-02-10; LABEL=${LABEL:-t08-10tenants}; OUT="$E9/k6"; mkdir -p "$OUT"
win() { command -v cygpath >/dev/null 2>&1 && cygpath -m "$1" || echo "$1"; }
SC="transactions.write transactions.read consolidation.read"
CLIENTS=(merchant-a-system merchant-b-system); MERCH=("$A" "$BM")
export TENANT_SECRET_0="$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" TENANT_SECRET_1="$KEYCLOAK_MERCHANT_B_SYSTEM_SECRET"
for i in 2 3 4 5 6 7 8 9; do CLIENTS+=("f9-load-0$i"); MERCH+=("f9000000-0000-4000-8000-00000000000$i"); done
cleanup() { for i in 2 3 4 5 6 7 8 9; do echo "remocao ${CLIENTS[$i]}: http=$(kc_delete "${CLIENTS[$i]}")"; done; rm -rf "$S"; }
trap cleanup EXIT
echo "# F9 14 - T-08 com 10 merchants - $(date -u +%FT%TZ)"
echo "gateway: imagem $(docker inspect -f '{{.Image}}' cashflow-gateway-1 | cut -c1-19) iniciado em $(docker inspect -f '{{.State.StartedAt}}' cashflow-gateway-1)"
sec "1. clients temporarios (segredo aleatorio, nunca impresso)"
for i in 2 3 4 5 6 7 8 9; do
  KC_NEW_SECRET=$(kc_secret); export KC_NEW_SECRET "TENANT_SECRET_$i=$KC_NEW_SECRET"
  echo "  ${CLIENTS[$i]} merchant_id=${MERCH[$i]}: http=$(kc_create "${CLIENTS[$i]}" "${MERCH[$i]}")"
done
unset KC_NEW_SECRET
sec "2. massa do oraculo em $DL (POST real; chaves fixas por merchant/linha, reexecucao vira replay)"
declare -a TOK
for i in 0 1 2 3 4 5 6 7 8 9; do
  v="TENANT_SECRET_$i"; TOK[$i]=$(token "${CLIENTS[$i]}" "${!v}" "$SC")
  [ "$i" -lt 2 ] && continue   # A e B ja tem a massa do 04-k6-seed (F7)
  echo "  merchant $i: CREDIT $i.10 -> $(post "${MERCH[$i]}" "${TOK[$i]}" "f9-seed-$i-1" '{"type":"CREDIT","amount":"'$i'.10","effectiveDate":"'$DL'","description":"f9-seed"}')" \
       "| CREDIT 20.00 -> $(post "${MERCH[$i]}" "${TOK[$i]}" "f9-seed-$i-2" '{"type":"CREDIT","amount":"20.00","effectiveDate":"'$DL'","description":"f9-seed"}')" \
       "| DEBIT 0.0$i -> $(post "${MERCH[$i]}" "${TOK[$i]}" "f9-seed-$i-3" '{"type":"DEBIT","amount":"0.0'$i'","effectiveDate":"'$DL'","description":"f9-seed"}')"
done
for i in 0 1 2 3 4 5 6 7 8 9; do echo -n "  merchant $i "; converge "${MERCH[$i]}" "${TOK[$i]}" "$DL" 60; done
unset TOK
sec "3. oraculo (fonte PostgreSQL) e lista de tenants"
EXPECT="{"; TENANTS="["
for i in 0 1 2 3 4 5 6 7 8 9; do
  read -r cr db n < <(src "${MERCH[$i]}" "$DL")
  e=$(python -I -c 'import json,sys; from decimal import Decimal as D; cr,db,n=sys.argv[1:4]; print(json.dumps({"credits":cr,"debits":db,"net":str(D(cr)-D(db)),"count":int(n)}))' "$cr" "$db" "$n")
  EXPECT+="$([ $i -gt 0 ] && echo ,)\"$i\":$e"; TENANTS+="$([ $i -gt 0 ] && echo ,){\"client\":\"${CLIENTS[$i]}\",\"merchant\":\"${MERCH[$i]}\"}"
done
EXPECT+="}"; TENANTS+="]"
echo "EXPECT=$EXPECT"; echo "TENANTS=$TENANTS"
sec "4. k6 ($K6_IMG) na rede cashflow_app, alvo https://gateway:8080"
STATS="$E9/14-stats.txt"
( while true; do echo "[stats $(date -u +%H:%M:%S)] $(docker stats --no-stream --format '{{.Name}} cpu={{.CPUPerc}} mem={{.MemUsage}}' | sed 's/^cashflow-//; s/-1 / /' | tr '\n' ';')"; sleep 1; done ) > "$STATS" 2>&1 &
SP=$!
SE=(); for i in 0 1 2 3 4 5 6 7 8 9; do SE+=(-e "TENANT_SECRET_$i"); done
MSYS_NO_PATHCONV=1 docker run --rm --name "f9-k6-$LABEL" --network cashflow_app -e SSL_CERT_FILE=/certs/ca.crt "${SE[@]}" \
  -e "LABEL=$LABEL" -e "DATE=$DL" -e "EXPECT=$EXPECT" -e "TENANTS=$TENANTS" -e GET_RATE=50 -e WARMUP_S=20 -e DURATION_S=120 \
  -v "$(win "$E9"):/scripts:ro" -v "$(win "$PKI_WIN/ca.crt"):/certs/ca.crt:ro" -v "$(win "$OUT"):/out" \
  "$K6_IMG" run --quiet --no-usage-report /scripts/f9-t08-10t.js 2>&1 | grep -vE '^\s*$'
RC=${PIPESTATUS[0]}
kill $SP 2>/dev/null; wait $SP 2>/dev/null
sec "5. respostas corretas por merchant (janela de medicao) e CPU maxima por container"
python -I -X utf8 - "$(win "$OUT/$LABEL.json")" "$(win "$STATS")" <<'PY'
import json, re, sys
s = json.load(open(sys.argv[1], encoding="utf-8"))
print("  2xx corretos por merchant:", s.get("get", {}).get("ok_by_tenant"))
mx = {}
for line in open(sys.argv[2], encoding="utf-8", errors="replace"):
    for name, cpu in re.findall(r"([\w.-]+) cpu=([\d.]+)%", line):
        mx.setdefault(name, []).append(float(cpu))
for k in sorted(mx):
    v = mx[k]; print(f"  {k}: amostras={len(v)} cpu_max={max(v):.2f}% cpu_media={sum(v)/len(v):.2f}%")
PY
echo "k6 rc=$RC"
exit $RC
