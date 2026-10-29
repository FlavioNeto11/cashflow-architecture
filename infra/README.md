# Infraestrutura local (Docker Compose)

Topologia **local de demonstração**, não de produção: **Docker Compose não é HA** (uma instância de cada, sem failover, sem TLS entre apps/banco/broker).
A topologia-alvo agnóstica está em `docs/architecture/deployment.md`. Estado: stack **pós-F5** (gateway YARP, JWT/OIDC com Keycloak, mTLS gateway -> APIs) reexecutada de zero na passada 3 de F6 (`docs/testing/evidence/f6/29b-gateway-scenarios-rerun.log`); imagens reconstruídas em F7 com as correções P1-1 e PF-03 (`docs/testing/evidence/f7/21-*.log`).

## Arquivos

| Caminho | Conteúdo |
|---|---|
| `compose/docker-compose.yml` | Base (projeto `cashflow`): `postgres`, `rabbitmq`, `keycloak`, `keycloak-init` (one-shot), `gateway`, `migrations-tx`, `migrations-cons`, `transactions-api`, `consolidation-api`, `consolidation-worker`, `recon-tool` (perfil `tools`); redes `edge`/`app`/`data` |
| `compose/docker-compose.dev.yml` | **Só desenvolvimento/verificação**: modo `DevelopmentHeaders` (`X-Dev-*`), APIs em HTTP puro e portas em `127.0.0.1`; **o gateway não é funcional neste perfil** (ver abaixo) |
| `compose/docker-compose.observability.yml` | Receptor OTLP (`otel-collector`) + variáveis OTLP nos 3 serviços .NET de negócio (o gateway não) |
| `compose/.env.example` | Nomes e valores óbvios de desenvolvimento; copie para `compose/.env` (ignorado pelo Git) |
| `docker/Dockerfile` | Multi-stage, usuário não-root; alvos `aspnet` (APIs, worker, gateway) e `runtime` (recon-tool); bases por digest |
| `pki/make-certs.sh` | CA local + certificados em `pki/out/` (ignorado pelo Git); F5 é dona |
| `postgres/init/*.sql` | Papéis, bancos, limites de conexão, timeouts. Rodam **só na criação do volume**. Privilégios por tabela vêm das migrações (`src/`) |
| `rabbitmq/` | `definitions.json` (topologia), `rabbitmq.conf`, `entrypoint.sh` (usuários a partir do ambiente) |
| `observability/otel-collector.yaml` | Configuração do receptor OTLP (exporter `debug` para stdout) |
| `keycloak/` | Realm `cashflow` (F5 é dona) |

## Pré-requisitos e como subir (da raiz do repositório)

```bash
cp infra/compose/.env.example infra/compose/.env      # uma vez; edite se quiser; NUNCA versionar o .env
bash infra/pki/make-certs.sh                          # uma vez: gera infra/pki/out/ (requer openssl >= 3.4). SEM isto gateway e APIs NAO sobem
# LINUX: make-certs.sh ajusta dono/modo das chaves para o uid dos containers (pede sudo se precisar; C-25).
#   Validado em container Linux (docs/testing/evidence/f7/19-make-certs-linux.log); host Linux nativo ainda nao testado.
B="-f infra/compose/docker-compose.yml"
D="-f infra/compose/docker-compose.dev.yml"
O="-f infra/compose/docker-compose.observability.yml"

docker compose $B --env-file infra/compose/.env up -d --build            # base: gateway https://127.0.0.1:8443, JWT + mTLS, APIs sem porta publicada
docker wait cashflow-keycloak-init-1                                     # F8: esperar o one-shot (imprime 0) antes de pedir token; --wait do compose nao o cobre
docker compose $B $D --env-file infra/compose/.env up -d --build         # dev: X-Dev-* + portas 127.0.0.1 (roteiro F4, SEM gateway funcional)
docker compose $B $D $O --env-file infra/compose/.env up -d --build      # dev + telemetria OTLP
docker compose $B $D $O --env-file infra/compose/.env config -q          # validar qualquer combinação sem subir
```

`config -q` das 4 combinações (base; base+dev; base+obs; base+dev+obs): `docs/testing/evidence/f6/24-compose-config.log`. Subida base+dev+obs e POST/consulta: `25-up-dev-obs-smoke.log`.

**Certificados em arquivos individuais (P1-1):** cada serviço monta só os arquivos de `pki/out/` de que precisa (`ca.key`, `foreign-ca.key` e `neg-*` nunca entram em container; `29-certs-file-mounts.log`). Se um arquivo montado não existir, o Docker cria um **diretório** no lugar e o serviço falha: rode `make-certs.sh` antes. Edite `pki/out/blocked-thumbprints.txt` **em lugar** (`>>`), não por substituição do arquivo.

**Permissões das chaves por ambiente (PF-02/C-25):** `make-certs.sh` aceita `PKI_OUT` (saída; padrão `pki/out`) e `PKI_KEY_UID`/`PKI_KEY_GID` (padrão 1654, usuário `app` das imagens .NET). Em Docker Desktop (Windows/macOS) o bind mount aparece como root 777 e o modo 600 basta. Em **host Linux** o bind mount preserva dono e modo, então o script torna as chaves de serviço legíveis ao uid 1654: como root faz `chown 1654:1654` + `600`; se o usuário pertence ao grupo 1654, `chgrp 1654` + `640`; caso contrário **falha (exit 3)** imprimindo o `sudo chown ... && sudo chmod 600 ...` exato, sem afrouxar para 644. `ca.key`, `foreign-ca.key` e `neg-*.key` ficam sempre 600 do dono. Validado só como mecanismo em container Linux sobre Docker Desktop (`docs/testing/evidence/f7/19-make-certs-linux.log`); **não** validado em host Linux nativo com o bind mount real do projeto.

### Portas publicadas no host (todas em `127.0.0.1`)

| Porta | Serviço | Observação |
|---|---|---|
| `8443` | gateway | **única** entrada de negócio; HTTPS com a CA local: `curl --cacert infra/pki/out/ca.crt` (Windows/Schannel: acrescente `--ssl-revoke-best-effort`, a CA local não publica CRL/OCSP) |
| `8080` | keycloak | HTTP, só para obter token na demonstração |
| `15672` | rabbitmq (gestão) | exige o usuário de **operação** (`RABBITMQ_OPS_*`); o usuário da aplicação não tem tag e recebe 401 |
| `18080`-`18084`, `15432` | APIs, worker, postgres | **só** com o override dev; o `otel-collector` não publica porta |

### Perfil dev: o gateway não é funcional

No override dev as APIs usam `DevelopmentHeaders` (ignoram o `Authorization`) e o gateway remove `X-Dev-*`: o caminho via gateway **não** funciona nesse perfil (C-F5-05); só o acesso direto `18080/18082`. O roteiro de F4 (`docs/testing/evidence/f4/verify-compose-scenarios.sh`) **só** vale com o override dev. O caminho completo (token real, TLS, mTLS) é o Compose base: `docs/testing/evidence/f5/gateway-compose-scenarios.sh` (faz `down -v` e sobe do zero).

### Keycloak, `keycloak-init` e reinício

- `KC_HOSTNAME=http://keycloak:8080` + `KC_HOSTNAME_STRICT=true`: um token pedido a `127.0.0.1:8080` já vem com `iss=http://keycloak:8080/realms/cashflow`; **não** é preciso editar `hosts`.
- O import do realm **não** substitui `${env.*}`. O one-shot `keycloak-init` (`restart: "no"`) aplica os secrets dos clientes confidenciais e a senha dos usuários demo via `kcadm.sh` após o import, sem senha em argv (`KC_CLI_PASSWORD` e stdin). Rotação: editar o `.env` e `docker compose $B --env-file infra/compose/.env run --rm keycloak-init`.
- O realm define o escopo `basic` (mapper `oidc-sub-mapper`) e o coloca nos `defaultClientScopes` dos três clientes: o import com `clientScopes` explícito não cria os escopos embutidos e, sem `basic`, o token Client Credentials vem **sem `sub`**. Não remover.
- `restart: unless-stopped` em `postgres`, `rabbitmq`, `keycloak`, `gateway`, `transactions-api`, `consolidation-api` e `consolidation-worker`; jobs em `"no"`. O Compose **não** reinicia container `unhealthy`. O reinício em si não foi exercitado.

**Cuidado - precedência de variáveis:** variáveis exportadas no shell (`set -a; . infra/compose/.env`) vencem o `--env-file`. Não exporte o `.env` no shell que roda `docker compose up` depois de editar senhas.

## Variáveis exigidas (`infra/compose/.env`) - nomes, sem valores

| Grupo | Variáveis |
|---|---|
| PostgreSQL | `POSTGRES_PASSWORD`, `MIGRATOR_PASSWORD`, `TX_APP_PASSWORD`, `CONS_RO_PASSWORD`, `CONS_WORKER_PASSWORD`, `RECON_RO_PASSWORD`, `RECON_WRITER_PASSWORD` |
| RabbitMQ (aplicação) | `RABBITMQ_DEFAULT_USER`, `RABBITMQ_DEFAULT_PASS` - usuário **sem tag**, permissões mínimas; usado por `transactions-api`, `consolidation-worker`, `recon-tool` |
| RabbitMQ (operação, A1) | `RABBITMQ_OPS_USER`, `RABBITMQ_OPS_PASS` - tag `administrator`, só humano (UI `127.0.0.1:15672`, API de gestão, `rabbitmqctl`). **Opcionais** (`${...:-}` no Compose): sem elas o broker sobe só com o usuário da aplicação e a UI não tem login. `.env.example` traz as duas linhas **comentadas**: por padrão o usuário de operação **não** é criado |
| Keycloak | `KEYCLOAK_ADMIN`, `KEYCLOAK_ADMIN_PASSWORD`; **obrigatórias (`:?`)** `KEYCLOAK_MERCHANT_A_SYSTEM_SECRET`, `KEYCLOAK_MERCHANT_B_SYSTEM_SECRET`, `KEYCLOAK_DEMO_USER_PASSWORD` - um `.env` antigo sem elas derruba o `config`/`up` inteiro, de propósito |

## PostgreSQL: least privilege (F6, P-DB-1)

- `init/` cria papéis e concede `CONNECT`; as tabelas não existem lá. Os privilégios por tabela vêm **das migrações** (`src/`), executadas por `migrator` (dono): `cons_ro` só `SELECT` em `daily_consolidations` (`ReadRoleLeastPrivilege`); `cons_worker` sem acesso a `reconciliation_runs`; `recon_ro` só `SELECT` em `transactions` (`ReconRoLeastPrivilege`, em `transactions_db`). **Não há passo manual** nem job separado.
- `init/02-databases.sql` **não** concede mais `SELECT` padrão a `cons_ro`/`recon_ro`; mantém os default privileges de `tx_app`.
- Validação: `down -v` + up do Compose base e consultas negativas com `permission denied` (`docs/testing/evidence/f6/23-pg-negative-after-p-db-1.log`, script `f6-pg-negative.sh`); testes de matriz estrita (`22-dotnet-test-persistence-p-db-1.log`: `ConsolidationGrantsTests` e `ReconRoGrantsTests`).
- Alterar `init/` ou senhas só vale em volume novo (`down -v`); para volume existente use `ALTER ROLE` (`docs/runbooks/rotacao-de-credenciais.md`).

## RabbitMQ: usuários e permissões (F6, C-20)

- Aplicação: `configure` vazio; `write` em `cashflow.transactions` e `cashflow.dlx`; `read` em `consolidation.transaction-registered` e `consolidation.dlq`. Verificado (`03-rabbit-app-user-verify.log`): tx-api publica, worker consome e copia para a DLX, sem `ACCESS_REFUSED`. O `recon-tool dlq redrive` **conectou e leu a DLQ**, mas só produziu recusas (`republicadas=0`): a **republicação** (permissão `write` em `cashflow.transactions` pelo recon-tool) **não foi demonstrada nesta fase**; foi demonstrada depois em F7 D3 com o usuário de aplicação (`docs/testing/evidence/f7/08-dlq-redelivery.log`; antes, em F4 rodada 4, com as permissões anteriores a F6).
- Operação: `administrator`, `.*`. O `entrypoint.sh` regenera `/etc/rabbitmq/definitions.d/90-users.json` a cada boot e o broker o **importa sobrescrevendo** tag, permissões e senha de usuário existente (verificado).
- **Desvio aceito (A10, decisão (b)):** um único usuário AMQP de aplicação (`RABBITMQ_DEFAULT_*`) serve tx-api, worker e recon-tool, com a união das permissões. O alvo de produção é um usuário por serviço (`target.md:315`, ADR-0003:26); a matriz está em `deployment.md` seção 8.
- O roteiro de F4 (`verify-compose-scenarios.sh`) já usa `RABBITMQ_OPS_*` na função `mq()`. Usuários e permissões reais: `26-rabbit-users-permissions.log` (`cashflow_ops [administrator]`, `dev_rabbit []` com permissões mínimas).
- A imagem do broker é `rabbitmq:4-management` fixada por digest (`21-image-digests.log`; `rabbitmqctl version` = 4.4.0).

## recon-tool (sob demanda, perfil `tools`)

```bash
C="docker compose -f infra/compose/docker-compose.yml -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env"
$C --profile tools run --rm recon-tool reconcile --merchant <uuid> --from 2026-10-01 --to 2026-10-31
$C --profile tools run --rm recon-tool rebuild   --merchant <uuid> --from 2026-10-03 --to 2026-10-03
$C --profile tools run --rm recon-tool dlq redrive --all          # ou --id <message-id>
```
Aviso inofensivo `Cannot load library libgssapi_krb5.so.2` (imagem sem Kerberos). O redrive usa AMQP com o usuário **da aplicação** (não precisa do usuário de operação).

## Observabilidade (F6)

`docker compose $B $D $O ... up -d` e depois `docker compose $B $D $O --env-file infra/compose/.env logs otel-collector` mostra logs, métricas e traces recebidos (exporter `debug`).
O que foi comprovado e o que **não** foi (ex.: não há span de AMQP, portanto o trace publisher → worker não é contínuo): `docs/architecture/deployment.md` seção 5 e `docs/testing/evidence/f6/10-otel-traces-summary.log`.

## Runbooks

`docs/runbooks/`: broker indisponível, worker parado/lag/consumo pausado, DLQ, reconciliação e rebuild, rotação de credenciais, certificados e revogação, IdP indisponível, diagnóstico de 401/403/429.

## Reset (DESTRUTIVO)

`docker compose $B $D --env-file infra/compose/.env down -v` apaga bancos, fila e usuários do broker. Sem `-v`, `down` preserva os volumes.

## Limitações conhecidas

- Redes `app`/`data` segmentam mas **não são `internal`** (A5 adiada): as portas publicadas são só loopback, mas um container comprometido tem egress. Teste de `internal: true`: `docs/testing/evidence/f6/16-internal-networks-test.log`. Redes internas + serviço de acesso do host no dev ficam como melhoria.
- Sem TLS entre apps e banco/broker/IdP (o Keycloak local fala HTTP, `AllowHttpMetadata=true` com aviso na partida); o `.env.example` traz valores de desenvolvimento só para loopback; três senhas do `.env` local foram rotacionadas em F7 (C-30) e o procedimento está em `docs/runbooks/rotacao-de-credenciais.md`.
- `restart: unless-stopped` não recupera container `unhealthy`; Compose não é HA.
- Imagens por digest não se atualizam sozinhas; procedimento no cabeçalho do `Dockerfile`.
- Telemetria só com o override; o gateway não exporta OTLP; sem span de AMQP.
- Desvio aceito: um único usuário AMQP de aplicação (ver acima).
