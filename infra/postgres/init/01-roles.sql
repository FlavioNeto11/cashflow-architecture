-- Papeis de banco do MVP (security.md 4.3; limites de conexao em target.md 7.2a, HIPOTESE numerica).
-- Senhas vem de variaveis de ambiente do container (\getenv); nada e literal neste arquivo.
-- Executa uma unica vez, na primeira inicializacao do volume (entrypoint do postgres).
\set ON_ERROR_STOP on

\getenv migrator_pw MIGRATOR_PASSWORD
\getenv tx_app_pw TX_APP_PASSWORD
\getenv cons_ro_pw CONS_RO_PASSWORD
\getenv cons_worker_pw CONS_WORKER_PASSWORD
\getenv recon_ro_pw RECON_RO_PASSWORD
\getenv recon_writer_pw RECON_WRITER_PASSWORD

CREATE ROLE migrator     LOGIN PASSWORD :'migrator_pw'     CONNECTION LIMIT 2;
CREATE ROLE tx_app       LOGIN PASSWORD :'tx_app_pw'       CONNECTION LIMIT 40;
CREATE ROLE cons_ro      LOGIN PASSWORD :'cons_ro_pw'      CONNECTION LIMIT 25;
CREATE ROLE cons_worker  LOGIN PASSWORD :'cons_worker_pw'  CONNECTION LIMIT 8;
CREATE ROLE recon_ro     LOGIN PASSWORD :'recon_ro_pw'     CONNECTION LIMIT 3;
CREATE ROLE recon_writer LOGIN PASSWORD :'recon_writer_pw' CONNECTION LIMIT 3;
