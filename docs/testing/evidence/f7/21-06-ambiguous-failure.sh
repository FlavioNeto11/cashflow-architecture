#!/usr/bin/env bash
# F7 Passo B item 6: falha ambigua de HTTP com Idempotency-Key (ver cabecalho de 21-06-ambiguous-failure.py).
# Uso: bash 21-06-ambiguous-failure.sh [tentativas] > 21-06-ambiguous-failure.log 2>&1 ; echo "exit=$?" >> ...
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
N="${1:-45}"; RUN=$(date -u +%H%M%S)
echo "# F7 21-06 - $(date -u +%FT%TZ) | gateway: $(docker inspect -f '{{.Image}}' cashflow-gateway-1 | cut -c1-19) | transactions-api: $(docker inspect -f '{{.Image}}' cashflow-transactions-api-1 | cut -c1-19)"
F7_SECRET="$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" F7_CA="$PKI_WIN/ca.crt" F7_GW=127.0.0.1:8443 F7_KC=127.0.0.1:8080 \
  python -I "$(cygpath -m "$REPO/docs/testing/evidence/f7/21-06-ambiguous-failure.py")" "$A" merchant-a-system 2026-07-03 "$RUN" "$N"
