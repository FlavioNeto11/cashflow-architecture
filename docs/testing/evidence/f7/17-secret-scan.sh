#!/usr/bin/env bash
# F7 17: varredura de segredos no repositorio inteiro (respeita .gitignore, sem filtro de extensao). Logica em 17-secret-scan.py.
# NUNCA imprime valores. Uso: bash docs/testing/evidence/f7/17-secret-scan.sh > docs/testing/evidence/f7/17-secret-scan.log 2>&1 ; echo "exit=$?" >> docs/testing/evidence/f7/17-secret-scan.log
cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." || exit 2
echo "# F7 17 - varredura de segredos (repositorio inteiro) - $(date -u +%FT%TZ)"
python -I docs/testing/evidence/f7/17-secret-scan.py "$PWD"
