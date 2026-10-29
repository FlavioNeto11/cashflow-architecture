#!/usr/bin/env bash
# F7 Passo B item 4: repeticao de b1 (rajada de 90) para medir a variabilidade dos 429 do merchant B. Mesmo procedimento do 21-04-all.sh.
D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
for r in r2 r3; do
  bash "$D/21-04-p1-1.sh" b1 $r </dev/null > "$D/21-04-b1-$r.log" 2>&1; echo "exit=$?" >> "$D/21-04-b1-$r.log"
  echo "$(date -u +%H:%M:%S) b1-$r -> $(tail -1 "$D/21-04-b1-$r.log")"; sleep 25
done
echo "fim $(date -u +%H:%M:%S)"
