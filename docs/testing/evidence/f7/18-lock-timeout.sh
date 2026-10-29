#!/usr/bin/env bash
# F7 (correcao pos-revisao, PF-01): confere, SOMENTE LEITURA, o lock_timeout efetivo da sessao do papel cons_worker (e dos demais papeis
# com timeouts), e que a connection string do worker no Compose nao o sobrescreve. A senha vem do ambiente (PGPASSWORD herdado do .env,
# repassado ao docker exec SEM valor na linha de comando) e nunca e impressa.
# Uso: bash 18-lock-timeout.sh > 18-lock-timeout.log 2>&1 ; echo "exit=$?" >> 18-lock-timeout.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
echo "# F7 18 - lock_timeout efetivo - $(date -u +%FT%TZ)"
sec "1. SHOW como o proprio cons_worker (login real, banco consolidation_db)"
export PGPASSWORD="$CONS_WORKER_PASSWORD"
docker exec -e PGPASSWORD cashflow-postgres-1 psql -h 127.0.0.1 -U cons_worker -d consolidation_db -At \
  -c "select current_user" -c "show lock_timeout" -c "show statement_timeout" -c "show idle_in_transaction_session_timeout" 2>&1 | sed 's/^/  cons_worker: /'
unset PGPASSWORD
sec "2. configuracao por papel (pg_db_role_setting via pg_roles.rolconfig)"
$PSQL_C -c "select rolname, rolconfig from pg_roles where rolname in ('cons_worker','cons_ro','recon_writer','recon_ro','tx_app') order by 1" | sed 's/^/  /'
sec "3. a connection string do worker no Compose nao define Options/Lock Timeout (grep no arquivo, senha omitida)"
grep -n 'ConnectionStrings__Default: "Host=postgres;Database=consolidation_db;Username=cons_worker' infra/compose/docker-compose.yml | sed -E 's/Password=[^;"]*/Password=<omitido>/'
echo "  ocorrencias de 'Options=' ou 'Lock Timeout' no compose: $(grep -ciE 'options=|lock timeout' infra/compose/docker-compose.yml)"
sec "4. codigo do worker: ocorrencias de SET lock_timeout no projeto do consumidor (leitura de src, nada alterado)"
grep -rniE 'lock_timeout' src/CashFlow.Consolidation.Worker src/CashFlow.Persistence 2>/dev/null | sed 's/^/  /' | head -5
echo "  total: $(grep -rniE 'lock_timeout' src/CashFlow.Consolidation.Worker src/CashFlow.Persistence 2>/dev/null | wc -l)"
