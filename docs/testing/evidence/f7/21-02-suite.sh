#!/usr/bin/env bash
# F7 Passo B item 2: build Release (warnaserror), suite completa da solucao e verificacao de formatacao, com contagem por projeto.
# A suite sobe containers Testcontainers (postgres, rabbitmq, keycloak e o reaper ryuk, PF-05) e compete por CPU com a stack:
# NAO rodar junto de carga (k6). Uso: bash 21-02-suite.sh > 21-02-suite.log 2>&1
cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." || exit 1
echo "# F7 21-02 - $(date -u +%FT%TZ) | dotnet $(dotnet --version)"
echo "\$ dotnet build CashFlow.slnx -c Release -warnaserror"
dotnet build CashFlow.slnx -c Release -warnaserror 2>&1 | tail -8
echo "exit-build=${PIPESTATUS[0]}"
echo "\$ dotnet test CashFlow.slnx -c Release --no-build   (resumo por projeto)"
dotnet test CashFlow.slnx -c Release --no-build 2>&1 | grep -E '^\s*(Passed!|Failed!|Skipped!)|Failed |error|Test summary|total:|passed:|failed:|skipped:' | cut -c1-260
echo "exit-test=${PIPESTATUS[0]}"
echo "\$ dotnet format CashFlow.slnx --verify-no-changes"
dotnet format CashFlow.slnx --verify-no-changes 2>&1 | tail -15
echo "exit-format=${PIPESTATUS[0]}"
echo "fim $(date -u +%FT%TZ)"
