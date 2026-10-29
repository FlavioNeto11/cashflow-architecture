#!/usr/bin/env bash
# F7 (T-08): sequencia completa de rodadas k6. Cada rodada gera k6/<label>.json e um log em 06-t08-<label>.log.
# Pausas de 20 s entre rodadas (recompoem baldes do rate limit e deixam o estado assentar).
D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
run() { local l="$1"; shift; bash "$D/05-k6-run.sh" "$l" "$@" </dev/null > "$D/06-t08-$l.log" 2>&1; echo "exit=$?" >> "$D/06-t08-$l.log"; echo "$(date -u +%H:%M:%S) $l -> $(tail -1 "$D/06-t08-$l.log")"; sleep 20; }
run post-baseline      GET_RATE=0  POST_RATE=10 WARMUP_S=10 DURATION_S=120
run get-single         TOKENS=A  GET_RATE=50 WARMUP_S=20 DURATION_S=120
run get-two-tokens     TOKENS=AB GET_RATE=50 WARMUP_S=20 DURATION_S=120
run get-single-plus-post TOKENS=A GET_RATE=50 POST_RATE=10 WARMUP_S=20 DURATION_S=120
run get-closing        TOKENS=A  GET_RATE=50 INCLUDE_CLOSING=1 WARMUP_S=20 DURATION_S=120
run probe-single-80    TOKENS=A  GET_RATE=80  WARMUP_S=0 DURATION_S=30
run probe-two-130      TOKENS=AB GET_RATE=130 WARMUP_S=0 DURATION_S=30
run get-single-10min   TOKENS=A  GET_RATE=50 WARMUP_S=20 DURATION_S=600
echo "fim $(date -u +%H:%M:%S)"
