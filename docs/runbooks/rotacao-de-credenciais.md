# Runbook: rotação de credenciais do `infra/compose/.env`

**Escopo:** ambiente local. Nunca imprimir, comitar ou colar valores. Em produção o alvo é cofre de segredos com rotação automática (`security.md`; não existe aqui).

## Fatos verificados (F6)
- **PostgreSQL:** as senhas dos papéis são aplicadas por `infra/postgres/init/01-roles.sql` **só na primeira criação do volume**. Trocar só o `.env` **não** altera o banco; é preciso `ALTER ROLE ... PASSWORD` e depois recriar o serviço do papel. **[EXECUTADO]** `14-rotation-pg.log`.
- **RabbitMQ:** o `entrypoint.sh` regenera `90-users.json` e o broker o importa **a cada boot**. **[EXECUTADO]** o import sobrescreve **tags, permissões e senha** de usuário existente
  (`02-rabbitmq-users-after-restart.log`: `administrator` → sem tag no volume já existente; `13-rotation-rabbit.log`: senha antiga recusada e nova aceita após recriar o broker).
  Usuário **removido** do `.env` NÃO é apagado do broker pela importação: remover com `rabbitmqctl delete_user`. **[NÃO VERIFICADO]**: afirmação derivada do comportamento documentado do `load_definitions`, sem log em F6.
- **ARMADILHA:** variáveis exportadas no shell (`set -a; . infra/compose/.env`) têm **precedência** sobre `--env-file`. Se o shell que roda `docker compose up` tiver exportado os valores antigos,
  os containers sobem com a senha antiga (aconteceu na primeira tentativa de F6). Ler o `.env` só em subshells ou abrir um shell novo antes do `up`.
- Serviços leem as variáveis só ao ser **criados** (`up -d --force-recreate`); conexões abertas seguem válidas com a senha antiga até caírem.
- `psql -h 127.0.0.1` dentro do container do PostgreSQL é `trust` (pg_hba da imagem): para validar senha use `-h postgres` (ou a porta publicada pelo perfil dev).

## PostgreSQL (exemplo: `CONS_WORKER_PASSWORD`) - sequência **[EXECUTADA]** por `docs/testing/evidence/f6/f6-rotation-pg.sh`
1. Gerar o novo valor sem exibi-lo e gravá-lo no `.env` (sed sobre a linha da variável).
2. Alterar o papel no servidor, passando o valor por variável de ambiente do processo (não na linha de comando do `psql`):
   `echo "ALTER ROLE cons_worker PASSWORD :'pw';" | PW="$NEW" docker exec -i -e PW cashflow-postgres-1 sh -c 'psql -U postgres -d postgres -v ON_ERROR_STOP=1 -v pw="$PW"'`
3. Recriar o consumidor do papel: `$C up -d --force-recreate --no-deps consolidation-worker`; verificar `list_consumers` e um POST com `dailyNet` mudando.
Papéis e donos: `tx_app` → `transactions-api`; `cons_ro` → `consolidation-api`; `cons_worker` → `consolidation-worker`; `recon_ro`/`recon_writer` → `recon-tool`; `migrator` → jobs `migrations-*`.
`POSTGRES_PASSWORD` (superusuário): `ALTER ROLE postgres PASSWORD ...`; nenhum serviço de aplicação o usa. **[NÃO EXECUTADO]**

## RabbitMQ - **[EXECUTADO]** por `docs/testing/evidence/f6/f6-rotation-rabbit.sh`
1. Trocar `RABBITMQ_DEFAULT_PASS` (aplicação) e/ou `RABBITMQ_OPS_PASS` (operação) no `.env`.
2. Recriar o broker **e** os consumidores da senha da aplicação: `$C up -d --force-recreate rabbitmq transactions-api consolidation-worker` (o `recon-tool` lê a variável a cada `run`).
3. Verificar: `rabbitmqctl authenticate_user <usuario> <senha>` (nova ok, antiga recusada; **limite:** a CLI recebe a senha em argv dentro do container, aceitável só em ambiente local descartável), `list_consumers`, um POST e o `dailyNet` mudando.

## Keycloak
`KEYCLOAK_ADMIN_PASSWORD` só é lido no bootstrap do primeiro start; depois, alterar no próprio Keycloak (F5 é dono da configuração do realm).

**Secrets dos clientes confidenciais e senha dos usuários demo** (`KEYCLOAK_MERCHANT_A_SYSTEM_SECRET`, `KEYCLOAK_MERCHANT_B_SYSTEM_SECRET`, `KEYCLOAK_DEMO_USER_PASSWORD`): o import do realm não substitui `${env.*}`, então o one-shot `keycloak-init` os aplica via `kcadm.sh`. Para rotacionar: editar o `.env` e `docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env run --rm keycloak-init` (idempotente; recusa secret com aspas, barra ou caractere de controle). Tokens já emitidos valem até `exp` (300 s). **[EXECUTADO]** a cada `up` no roteiro de F5 (`29b-gateway-scenarios-rerun.log`, seção 1: secrets aplicados e tokens obtidos); a rotação com valor *diferente* não foi exercitada.

## Pré-requisito dos scripts de rotação
`f6-rotation-pg.sh` e `f6-rotation-rabbit.sh` exigem a variável `OVR` com um **override scratch** que entrega `RABBITMQ_OPS_*` ao broker (cópia: `docs/testing/evidence/f6/scratch-rabbit-ops-override.yml`, ajuste o caminho); sem ele abortam. Desde A1 (passada 3) o Compose base repassa `RABBITMQ_OPS_*` ao broker (`:-`), então o override scratch é redundante; os scripts, anteriores a A1, ainda o exigem e não foram ajustados nem reexecutados. `f6-rotation-rabbit.sh` também exige `BK` (backup do `.env` fora do repositório). Os scripts foram editados após a execução registrada (segredos fora do argv do host) e **não foram reexecutados**.

## Execução real (F6)
Logs: `13-rotation-rabbit.log`, `14-rotation-pg.log`, `02-rabbitmq-users-after-restart.log` em `docs/testing/evidence/f6/`. Executadas em 2026-10-29 **antes de F5** (perfil dev) e **não refeitas** depois. Rotação de certificados é de F5 (`infra/pki`).
