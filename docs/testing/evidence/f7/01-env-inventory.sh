#!/usr/bin/env bash
# F7: inventario do ambiente (versoes, recursos, estado da stack). Sem valores de .env (so nomes e tamanhos).
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
echo "data UTC: $(date -u +%FT%TZ)"
echo "host: $(uname -srm) | Windows $(powershell.exe -NoProfile -Command '(Get-CimInstance Win32_OperatingSystem).Version' </dev/null 2>/dev/null | tr -d '\r')"
echo "git HEAD: $(git rev-parse --short HEAD) | alteracoes nao commitadas: $(git status --short | wc -l)"
echo "docker: $(docker --version) | $(docker info --format 'CPUs={{.NCPU}} Mem={{.MemTotal}}B OS={{.OperatingSystem}}')"
echo "dotnet: $(dotnet --version) | curl: $(curl --version | head -1) | openssl: $(openssl version)"
echo "k6 no host: $(command -v k6 || echo 'ausente (usa grafana/k6 em container)')"
echo "trivy: $(command -v trivy || echo ausente)"
echo "--- stack (Compose BASE, sem overrides)"
$C ps --format 'table {{.Name}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}' | cut -c1-200
echo "--- variaveis do .env presentes (so nomes e tamanho)"
for v in KEYCLOAK_MERCHANT_A_SYSTEM_SECRET KEYCLOAK_MERCHANT_B_SYSTEM_SECRET RABBITMQ_OPS_USER RABBITMQ_OPS_PASS; do
  eval "x=\${$v:-}"
  if [ -n "$x" ]; then echo "$v: definido (${#x} chars)"; else echo "$v: AUSENTE"; fi
done
echo "--- contadores no momento desta regeneracao do log (a stack ja vinha sendo usada por F5/F6 e pelos roteiros F7 anteriores; nao sao contadores "iniciais")"
echo "transactions: $($PSQL_T -c 'select count(*) from transactions') | outbox pendentes: $($PSQL_T -c 'select count(*) from outbox_messages where published_at is null')"
qctl
