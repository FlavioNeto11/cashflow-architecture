-- Dois bancos no mesmo servidor (opcao C, target.md 4). O dono e o papel migrator: no PostgreSQL 15+
-- so o dono do banco cria objetos no schema public, e so os jobs --migrate usam esse papel.
\set ON_ERROR_STOP on

CREATE DATABASE transactions_db  OWNER migrator;
CREATE DATABASE consolidation_db OWNER migrator CONNECTION LIMIT 40;  -- target.md 7.2a

REVOKE CONNECT ON DATABASE transactions_db  FROM PUBLIC;
REVOKE CONNECT ON DATABASE consolidation_db FROM PUBLIC;
-- Papeis de aplicacao nao criam tabelas temporarias nem entram no banco administrativo.
REVOKE TEMPORARY ON DATABASE transactions_db, consolidation_db FROM PUBLIC;
REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;

GRANT CONNECT ON DATABASE transactions_db  TO migrator, tx_app, recon_ro;
GRANT CONNECT ON DATABASE consolidation_db TO migrator, cons_ro, cons_worker, recon_writer;

-- O schema "ef" (historico de migracoes do EF, criado pelo migrator) fica FORA dos privilegios abaixo: nenhum papel de
-- aplicacao recebe USAGE nele, e todos os ALTER DEFAULT PRIVILEGES a seguir sao restritos a "IN SCHEMA public".
-- Privilegios sobre tabelas futuras (criadas pelo migrator nas migracoes). Somente os casos sem ambiguidade
-- entram aqui; as concessoes por tabela de cons_worker e recon_writer (security.md 4.3: DML apenas em
-- daily_consolidations/processed_messages/reconciliation_runs) vem das migracoes (src/).
-- F6 P-DB-1: recon_ro e cons_ro NAO recebem privilegio padrao aqui. O SELECT minimo (recon_ro: transactions;
-- cons_ro: daily_consolidations) e concedido pelas migracoes ReconRoLeastPrivilege e ReadRoleLeastPrivilege, executadas
-- por migrator (dono das tabelas); um 'down -v' + up reproduz a mesma postura sem passo manual.
\connect transactions_db
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tx_app;
ALTER DEFAULT PRIVILEGES FOR ROLE migrator IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO tx_app;

\connect consolidation_db
