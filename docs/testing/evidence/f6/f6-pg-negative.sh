#!/usr/bin/env bash
# Consultas positivas e negativas por papel (senhas do .env, nunca impressas). Cada negativa deve falhar com "permission denied".
# NOTA (passada documental pos-revisao, 2026-10-29): este script foi EDITADO depois da execucao cujo log esta nesta pasta; as edicoes NAO foram reexecutadas
# Nota: -h 127.0.0.1 dentro do container e "trust" no pg_hba da imagem (nao valida senha); serve para provar PRIVILEGIOS, nao autenticacao (ver f6-rotation-pg.sh).
set -u
cd "$(dirname "$0")/../../../.." || exit 1
set -a; . infra/compose/.env; set +a
run() { # run <role> <password> <db> <sql>
  printf '%s@%s> %s\n   ' "$1" "$3" "$4"
  PGPASSWORD="$2" docker exec -i -e PGPASSWORD cashflow-postgres-1 psql -h 127.0.0.1 -U "$1" -d "$3" -At -c "$4" 2>&1 | head -2 | tr '\n' ' '; echo
}
echo "## consolidation_db"
run cons_ro "$CONS_RO_PASSWORD" consolidation_db "SELECT count(*) FROM daily_consolidations"            # esperado OK
run cons_ro "$CONS_RO_PASSWORD" consolidation_db "SELECT count(*) FROM processed_messages"              # esperado denied
run cons_ro "$CONS_RO_PASSWORD" consolidation_db "SELECT count(*) FROM reconciliation_runs"             # esperado denied
run cons_ro "$CONS_RO_PASSWORD" consolidation_db "UPDATE daily_consolidations SET credits=credits"      # esperado denied
run cons_worker "$CONS_WORKER_PASSWORD" consolidation_db "SELECT count(*) FROM daily_consolidations"    # esperado OK
run cons_worker "$CONS_WORKER_PASSWORD" consolidation_db "SELECT count(*) FROM processed_messages"      # esperado OK
run cons_worker "$CONS_WORKER_PASSWORD" consolidation_db "SELECT count(*) FROM reconciliation_runs"     # esperado denied
run cons_worker "$CONS_WORKER_PASSWORD" consolidation_db "DELETE FROM processed_messages"               # esperado denied
run recon_writer "$RECON_WRITER_PASSWORD" consolidation_db "SELECT count(*) FROM reconciliation_runs"   # esperado OK
run recon_writer "$RECON_WRITER_PASSWORD" consolidation_db "UPDATE reconciliation_runs SET divergences=0" # esperado denied
echo "## transactions_db"
run recon_ro "$RECON_RO_PASSWORD" transactions_db "SELECT count(*) FROM transactions"                   # esperado OK
run recon_ro "$RECON_RO_PASSWORD" transactions_db "SELECT count(*) FROM outbox_messages"                # esperado denied
run recon_ro "$RECON_RO_PASSWORD" transactions_db "SELECT count(*) FROM idempotency_keys"               # esperado denied
run recon_ro "$RECON_RO_PASSWORD" transactions_db "DELETE FROM transactions"                            # esperado denied
run tx_app "$TX_APP_PASSWORD" transactions_db "UPDATE transactions SET amount=amount"                   # esperado denied (imutabilidade, C-05)
run tx_app "$TX_APP_PASSWORD" transactions_db "SELECT count(*) FROM transactions"                       # esperado OK
