#!/usr/bin/env bash
# F7 Passo B itens 4(c) e 5: rodadas na stack BASE (sem coletor OTLP, gateway recriado so com o Compose base) com docker stats denso
# (todos os containers, inclusive k6, worker e rabbitmq; amostra bruta em 21-05-stats-<label>.txt).
D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
run() { local l="$1"; shift; STATS_OUT="$D/21-05-stats-$l.txt" bash "$D/05-k6-run.sh" "$l" "$@" </dev/null > "$D/21-05-$l.log" 2>&1; echo "exit=$?" >> "$D/21-05-$l.log"; echo "$(date -u +%H:%M:%S) $l -> $(tail -1 "$D/21-05-$l.log")"; sleep 20; }
run base-get50-120s        TOKENS=A GET_RATE=50 WARMUP_S=10 DURATION_S=120
run base-get50-post10-120s TOKENS=A GET_RATE=50 POST_RATE=10 WARMUP_S=10 DURATION_S=120
echo "fim $(date -u +%H:%M:%S)"
