#!/usr/bin/env bash
# F7 Passo B item 4 (P1-1): prova NA STACK de que trafego anonimo / rajada de um merchant nao derruba o autenticado.
# Pre-requisito: stack BASE + 21-04-otel-override.yml no ar (coletor OTLP temporario que expoe rate_limited_total do GATEWAY, porque
# a stack base nao exporta metricas). Orquestra, por caso: instantaneo dos contadores -> geradores em paralelo -> instantaneo -> diferenca.
# ORACULO (verificacao): 0 respostas 429 para autenticados (merchant NAO responsavel pela rajada) e perda (ADR-0009) <= 5 por cento.
#   (a) inundacao anonima > 150/s no consolidado, simultanea ao k6 autenticado a 50 RPS;
#   (b) rajada > 60 req em < 1 s de UM merchant (A): 429 para o PROPRIO A e ESPERADO (limite 60/s por merchant); o oraculo de
#       isolamento e o merchant B em paralelo (20 req/s, 0 x 429). b2 usa rajada acima do teto GLOBAL (120/s) e mede o que ocorre com B;
#   (c) k6 a 50 RPS, delta de rate_limited_total esperado 0.
# Uso: bash 21-04-p1-1.sh <caso> [sufixo]   (caso: c | a1 | a2 | b1 | b2; sufixo opcional para repeticoes)   > 21-04-<caso>.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
E="$REPO/docs/testing/evidence/f7"; OUT="$E/k6"; K6_IMG=grafana/k6@sha256:3e4186aea62e11a45560bfc51e19eeafe82fc07ce069c78aff44b7ff893d9a5e
CASE="${1:?caso}"
win() { cygpath -m "$1"; }
snap() { sleep 4; docker logs --since 8s cashflow-otelcol-1 2>&1 | python -I "$(win "$E/21-04-counters.py")" snap > "$1"; }
gen() { # gen <label> <VAR=valor ...>: gerador auxiliar em container proprio (um IP proprio na rede cashflow_app)
  local l="$1"; shift; local a=(); for kv in "$@"; do a+=(-e "$kv"); done
  MSYS_NO_PATHCONV=1 docker run --rm --name "f7-gen-$l" --network cashflow_app -e SSL_CERT_FILE=/certs/ca.crt \
    -e KEYCLOAK_MERCHANT_A_SYSTEM_SECRET -e KEYCLOAK_MERCHANT_B_SYSTEM_SECRET -e "LABEL=$l" "${a[@]}" \
    -v "$(win "$E"):/scripts:ro" -v "$(win "$PKI_WIN/ca.crt"):/certs/ca.crt:ro" -v "$(win "$OUT"):/out" \
    "$K6_IMG" run --quiet --no-usage-report /scripts/21-04-load.js 2>&1 | grep -vE '^\s*$'
}
echo "# F7 21-04 caso=$CASE - $(date -u +%FT%TZ)"
echo "gateway: $(docker inspect -f '{{.Image}}' cashflow-gateway-1 | cut -c1-19) iniciado em $(docker inspect -f '{{.State.StartedAt}}' cashflow-gateway-1) | override OTLP: $(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' cashflow-gateway-1 | grep -c '^OTEL_EXPORTER_OTLP_ENDPOINT=') (1 = coletor ligado)"
echo "limites do gateway (GatewaySettings.cs; o Compose nao sobrescreve): escrita 20/s burst 40; leitura 30/s; consolidado 60/s POR MERCHANT; teto GLOBAL do consolidado 120/s (so autenticados); anonimo 20/s por IP"
LABEL="p11-$CASE${2:+-$2}"
snap "$S/antes.json"
echo "contadores ANTES: $(cat "$S/antes.json")"
STATS_OUT="$E/21-05-stats-$LABEL.txt"; export STATS_OUT
case "$CASE" in
  c)  bash "$E/05-k6-run.sh" "$LABEL" TOKENS=A GET_RATE=50 WARMUP_S=10 DURATION_S=60 </dev/null ;;
  a1|a2)
      RATE=200; [ "$CASE" = a2 ] && RATE=600
      echo ">>> inundacao anonima ${RATE}/s (gerador f7-gen-$LABEL, IP proprio) + k6 autenticado 50 RPS merchant A, janela 60 s"
      ( gen "$LABEL-anon" MODE=flood RATE=$RATE DURATION_S=90 MERCHANT=A > "$S/gen.txt" 2>&1 ) &
      GP=$!; sleep 3
      bash "$E/05-k6-run.sh" "$LABEL" TOKENS=A GET_RATE=50 WARMUP_S=10 DURATION_S=60 </dev/null
      wait $GP; echo "--- gerador anonimo"; cat "$S/gen.txt" ;;
  b1|b2)
      N=90; [ "$CASE" = b2 ] && N=200
      echo ">>> 3 rajadas de $N req (merchant A, ~simultaneas) + merchant B (vitima) a 20 req/s, janela 50 s"
      ( gen "$LABEL-burst" MODE=burst BURST_N=$N BURSTS=3 FIRST_BURST_S=25 GAP_S=10 MERCHANT=A > "$S/gen.txt" 2>&1 ) &
      GP=$!
      bash "$E/05-k6-run.sh" "$LABEL" TOKENS=B GET_RATE=20 WARMUP_S=10 DURATION_S=50 </dev/null
      wait $GP; echo "--- gerador de rajada (merchant A)"; cat "$S/gen.txt" ;;
  *) echo "caso invalido"; exit 2 ;;
esac
snap "$S/depois.json"
echo "contadores DEPOIS: $(cat "$S/depois.json")"
echo "--- DIFERENCA gateway-side (antes -> depois)"
python -I "$(win "$E/21-04-counters.py")" diff "$S/antes.json" "$S/depois.json"
