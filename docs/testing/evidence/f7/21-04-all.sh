#!/usr/bin/env bash
# F7 Passo B item 4: sequencia dos casos a1, a2, b1, b2 (o caso c ja rodou antes). Pausa de 25 s entre casos (baldes de rate limit).
D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
for c in a1 a2 b1 b2; do
  bash "$D/21-04-p1-1.sh" $c </dev/null > "$D/21-04-$c.log" 2>&1; echo "exit=$?" >> "$D/21-04-$c.log"
  echo "$(date -u +%H:%M:%S) $c -> $(tail -1 "$D/21-04-$c.log")"; sleep 25
done
echo "fim $(date -u +%H:%M:%S)"
