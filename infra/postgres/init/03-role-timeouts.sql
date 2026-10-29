-- Timeouts por papel, SOMENTE nos papeis do consolidado e da reconciliacao (target.md 7.2a, HIPOTESES):
-- tx_app e migrator ficam sem statement_timeout para nao cortar o registro nem o DDL.
\set ON_ERROR_STOP on

ALTER ROLE cons_ro      SET statement_timeout = '2s';
ALTER ROLE cons_worker  SET statement_timeout = '10s';
ALTER ROLE cons_worker  SET lock_timeout      = '5s';
ALTER ROLE recon_writer SET statement_timeout = '60s';
ALTER ROLE recon_writer SET lock_timeout      = '2s';
ALTER ROLE recon_ro     SET statement_timeout = '60s';

ALTER ROLE cons_ro      SET idle_in_transaction_session_timeout = '30s';
ALTER ROLE cons_worker  SET idle_in_transaction_session_timeout = '30s';
ALTER ROLE recon_ro     SET idle_in_transaction_session_timeout = '30s';
ALTER ROLE recon_writer SET idle_in_transaction_session_timeout = '30s';
