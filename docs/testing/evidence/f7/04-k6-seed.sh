#!/usr/bin/env bash
# F7 (T-08): massa conhecida para o oraculo de exatidao do k6, criada pelo caminho REAL (POST pelo gateway) e aguardada ate
# o consolidado convergir. Data fixa DL=2026-02-10 (merchants A e B). Idempotente: chaves fixas por merchant/linha.
# Uso: bash docs/testing/evidence/f7/04-k6-seed.sh > docs/testing/evidence/f7/04-k6-seed.log 2>&1
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
DL=2026-02-10
echo "# F7 04 - massa do oraculo T-08 em $DL - $(date -u +%FT%TZ)"
mktokens
seed() { # seed <merchant> <token> <chave> <tipo> <valor>
  local c; c=$(post "$1" "$2" "f7-seed-$3" '{"type":"'$4'","amount":"'$5'","effectiveDate":"'$DL'","description":"f7-seed"}')
  echo "  $4 $5 (chave f7-seed-$3) -> $c$(grep -qi '^idempotency-replayed' "$H" && echo ' [replay: ja existia]')"; }
echo "--- merchant A"; seed "$A" "$TA" a1 CREDIT 100.00; seed "$A" "$TA" a2 CREDIT 35.20; seed "$A" "$TA" a3 CREDIT 10.00; seed "$A" "$TA" a4 DEBIT 20.00; seed "$A" "$TA" a5 DEBIT 5.00
echo "--- merchant B"; seed "$BM" "$TB" b1 CREDIT 50.00; seed "$BM" "$TB" b2 DEBIT 12.34
converge "$A" "$TA" "$DL" 60; converge "$BM" "$TB" "$DL" 60
echo "fonte A: $(src "$A" "$DL") | consolidado A: $(daily "$A" "$TA" "$DL")"
echo "fonte B: $(src "$BM" "$DL") | consolidado B: $(daily "$BM" "$TB" "$DL")"
echo "closingBalance A: $(daily "$A" "$TA" "$DL" '&include=closingBalance')"
