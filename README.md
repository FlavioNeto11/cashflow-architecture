# Desafio de Arquitetura de Soluções — Fluxo de Caixa

Controle de lançamentos de débito e crédito por comerciante (merchant) e consulta do saldo diário consolidado, com o registro de lançamentos funcionando mesmo com a consolidação fora do ar.

## Estado

- **Implementado localmente**: três processos .NET 10 (`transactions-api`, `consolidation-worker`, `consolidation-api`), gateway YARP, Keycloak, PostgreSQL e RabbitMQ em Docker Compose. Arquitetura aprovada em G1 (`docs/project/approvals.md`).
- **Auditoria final**: `docs/project/final-audit.md`.
- **Aceite final (G2)**: aceito pelo responsável em 2026-10-29 (`docs/project/approvals.md`).
- **Estado real de cada item**: `docs/architecture/implemented.md`, com os rótulos VALIDADO, PARCIAL, IMPLEMENTADO NÃO VALIDADO, PROJETADO e FUTURO.
- **Escopo das evidências**: tudo foi medido numa máquina local com Docker Desktop (Windows, Git Bash). Nada foi implantado em nuvem e nenhuma medição vale para produção. O CI (GitHub Actions, `.github/workflows/ci.yml`) compila e roda as 4 suítes, inclusive as de Testcontainers; a stack Compose e o k6 não rodam no CI.

## Quickstart

Pré-requisitos:
- Docker com Compose v2;
- Bash (Git Bash no Windows);
- `openssl` ≥ 3.4 (para `make-certs.sh`);
- `curl` e `python` (para o exemplo abaixo);
- .NET SDK 10 só para build e testes fora dos containers.

Comandos executados da raiz do repositório:

```bash
[ -f infra/compose/.env ] || cp infra/compose/.env.example infra/compose/.env
```

```bash
bash infra/pki/make-certs.sh
```

```bash
docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env up -d --build --wait
```

```bash
docker wait cashflow-keycloak-init-1
```

```bash
docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env ps
```

`--wait` espera os healthchecks, mas não o job one-shot `keycloak-init`, que grava os segredos dos clientes no Keycloak; `docker wait` bloqueia até ele terminar e deve imprimir `0`. Sem essa espera, o token falha e a API devolve 401 (achado da primeira execução do quickstart, `docs/testing/evidence/f8/01a-readme-quickstart-tentativa1.log`). `make-certs.sh` precisa rodar antes do `up`, porque o gateway e as APIs montam os certificados de `infra/pki/out/`. Portas no host, todas em loopback:

| Porta | Serviço |
|---|---|
| `127.0.0.1:8443` | gateway, a única entrada das APIs |
| `127.0.0.1:8080` | Keycloak |
| `127.0.0.1:15672` | UI do RabbitMQ (o usuário de operação é opcional no `.env.example`; sem ele, não há login na UI) |

Detalhes, overrides de desenvolvimento e observabilidade: `infra/README.md`.

### Primeiro lançamento e consulta

O trecho abaixo roda num subshell: carrega o `.env` sem imprimir valores, obtém um token `client_credentials` do merchant A, registra um crédito e consulta o consolidado do dia. O segredo vai ao `curl` por stdin, e o token não é impresso. Com `set -euo pipefail` e `--fail-with-body`, qualquer falha (token, POST ou GET) interrompe o trecho com código diferente de zero. A data é a de negócio em `America/Sao_Paulo` (`BusinessCalendar.cs`); com a data UTC, entre 00:00 e 03:00 UTC o POST seria rejeitado como data futura.

```bash
(
  set -euo pipefail
  set -a; . <(tr -d '\r' < infra/compose/.env); set +a
  CA=infra/pki/out/ca.crt; REVOKE=; curl --version | grep -q Schannel && REVOKE=--ssl-revoke-best-effort
  GW=https://127.0.0.1:8443/api/v1; M=33333333-3333-4333-8333-333333333333; D=$(date -u -d '-3 hours' +%F)  # data de negócio em America/Sao_Paulo (UTC-3)
  TOKEN=$(printf 'client_secret=%s' "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" | curl -sS -f -d @- \
      -d grant_type=client_credentials -d client_id=merchant-a-system \
      -d 'scope=transactions.write transactions.read consolidation.read' \
      http://127.0.0.1:8080/realms/cashflow/protocol/openid-connect/token \
    | python -I -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')
  curl -sS --fail-with-body $REVOKE --cacert "$CA" -w '\nHTTP %{http_code}\n' -X POST "$GW/merchants/$M/transactions" \
    -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: readme-$(date +%s)" -H 'Content-Type: application/json' \
    -d "{\"type\":\"CREDIT\",\"amount\":\"100.00\",\"effectiveDate\":\"$D\",\"description\":\"exemplo do README\"}"
  sleep 2
  curl -sS --fail-with-body $REVOKE --cacert "$CA" -w '\nHTTP %{http_code}\n' "$GW/merchants/$M/consolidations/daily?date=$D" \
    -H "Authorization: Bearer $TOKEN"
)
```

Resultado esperado: `HTTP 201` no POST e `HTTP 200` no GET, com o crédito no consolidado do dia. A consolidação é assíncrona: se o GET vier antes do worker processar o evento, repita a consulta. Uma execução real deste trecho está em `docs/testing/evidence/f8/01-readme-quickstart.log`.

Comportamentos úteis para conferir:
- A mesma `Idempotency-Key` com o mesmo corpo devolve a resposta original com `Idempotency-Replayed: true`; com outro corpo, 422; com a mesma chave ainda em processamento, 409 (ADR-0005).
- Uma data futura é rejeitada.
- Um `merchantId` diferente do token devolve 404.
- Com `consolidation-worker` e `consolidation-api` parados, o POST continua 201 e o GET do consolidado dá 502.

### Credenciais

- `infra/compose/.env.example` traz valores de **desenvolvimento**, válidos só para a stack local em loopback.
- O `.env` real não é versionado (`.gitignore`).
- Rotação: `docs/runbooks/rotacao-de-credenciais.md`.
- Keycloak roda em `start-dev`, o que não é configuração de produção.

## Build e testes

```bash
dotnet build CashFlow.slnx -c Release -warnaserror
```

```bash
dotnet test CashFlow.slnx -c Release --no-build
```

Na última execução registrada foram 364 testes aprovados (175 + 71 + 69 + 49; `docs/testing/evidence/f9/06-suite-transactions-api.log` a `09-suite-persistence.log` e o CI em `docs/testing/evidence/f9/21-ci-github-actions.log`). Os testes de persistência usam Testcontainers e precisam do Docker.

Cenários fim a fim e carga:
- Os scripts ficam em `docs/testing/evidence/f7/` e usam a stack no ar e `f7-common.sh`.
- Foram escritos e executados em Git Bash no Windows; em Linux não foram testados.
- Para a carga de 50 RPS com k6 em container fixado por digest, rode `bash docs/testing/evidence/f7/04-k6-seed.sh` e depois `bash docs/testing/evidence/f7/05-k6-run.sh <rotulo>`.
- A lista de cenários, comandos e resultados está em `docs/testing/evidence-log.md`.

## Navegação

| Necessidade | Onde |
|---|---|
| Requisitos: fonte, RF/RNF, aceites, rastreabilidade | `docs/requirements/` (`source-of-truth.md`, `RF.md`, `RNF.md`, `acceptance.md`, `traceability.csv`) |
| Arquitetura-alvo (C4, segurança, deployment, custos) | `docs/architecture/target.md`, `security.md`, `deployment.md`, `cost-estimate.md` |
| O que de fato existe e roda | `docs/architecture/implemented.md` |
| Decisões | `docs/adr/` (ADR-0001 a ADR-0011), `docs/project/approvals.md`, `docs/project/decisions-needed.md` |
| Código | `src/` (serviços), `tests/` (suítes), `infra/` (Compose, PKI, Keycloak) |
| Evidências | `docs/testing/evidence-log.md`, `docs/testing/evidence/f2` … `f8`, `docs/testing/test-plan.md` |
| Operação | `docs/runbooks/` |
| Lacunas e riscos | `docs/project/final-audit.md`, `docs/project/risk-register.csv` |

## Estados de realização

- `PROPOSTO`: desenho ou hipótese, não aprovado.
- `APROVADO`: decisão revista pelo responsável, ainda não necessariamente implementada.
- `IMPLEMENTADO_NAO_VALIDADO`: existe código, mas não há evidência suficiente.
- `VALIDADO`: execução ou teste com comando, ambiente, data, resultado e evidência.
- `PARCIAL`: executado em parte ou com ressalva declarada.
- `FUTURO`: fora do escopo implementado, registrado como evolução.

## Controle de escopo

Há três trilhas separadas: **arquitetura-alvo** (`target.md`), **protótipo implementado** (`implemented.md`) e **evoluções futuras**.

Licença: a escolha de uma licença para o produto é responsabilidade do proprietário do repositório.
