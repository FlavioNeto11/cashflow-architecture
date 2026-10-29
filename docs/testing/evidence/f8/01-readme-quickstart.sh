#!/usr/bin/env bash
# F8: executa o quickstart do README.md da raiz a partir de volumes vazios (down -v).
# Os blocos ```bash entre "## Quickstart" e "### Credenciais" sao extraidos do README e executados na ordem, sem edicao.
# Nao imprime segredos nem tokens (o proprio trecho do README nao os imprime).
# Uso (da raiz): bash docs/testing/evidence/f8/01-readme-quickstart.sh > docs/testing/evidence/f8/01-readme-quickstart.log 2>&1
set -u
cd "$(dirname "${BASH_SOURCE[0]}")/../../../.."
C="docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env"
echo "# F8 01 - quickstart do README - inicio $(date -u +%FT%TZ)"
echo "# host: $(uname -s) | $(docker version --format 'docker {{.Server.Version}}' 2>&1) | $(docker compose version 2>&1)"
echo "# .env: $([ -f infra/compose/.env ] && echo 'ja existia (credenciais rotacionadas em F7, C-30); o primeiro bloco nao o sobrescreve' || echo ausente)"
echo "# certificados: $([ -f infra/pki/out/ca.crt ] && echo 'infra/pki/out/ca.crt ja existia; make-certs.sh nao regenera sem --force' || echo ausentes)"
echo
echo "## 0. estado zerado: down -v (remove containers e volumes do projeto cashflow)"
$C --profile tools down -v --remove-orphans 2>&1
echo "volumes restantes do projeto: $(docker volume ls -q --filter label=com.docker.compose.project=cashflow | wc -l)"
echo
python -I -X utf8 - > "${TMPDIR:-/tmp}/f8-readme-blocks.txt" <<'PY'
import re
t = open("README.md", encoding="utf-8").read()
sec = t[t.index("## Quickstart"):t.index("### Credenciais")]
for b in re.findall(r"```bash\n(.*?)```", sec, re.S):
    print(b.replace("\n", "\x1f").rstrip("\x1f"))
PY
n=0
while IFS= read -r line; do
  n=$((n+1)); block=$(printf '%s' "$line" | tr '\037' '\n')
  echo "## bloco $n do README"
  printf '%s\n' "$block" | sed 's/^/  $ /'
  s=$(date +%s)
  bash -c "$block" 2>&1
  echo "-> saida $? em $(( $(date +%s) - s )) s"
  echo
done < "${TMPDIR:-/tmp}/f8-readme-blocks.txt"
echo "## conferencia: segunda consulta do consolidado (sem repetir o POST)"
(
  set -a; . <(tr -d '\r' < infra/compose/.env); set +a
  REVOKE=; curl --version | grep -q Schannel && REVOKE=--ssl-revoke-best-effort
  D=$(date -u -d '-3 hours' +%F)
  TOKEN=$(printf 'client_secret=%s' "$KEYCLOAK_MERCHANT_A_SYSTEM_SECRET" | curl -sS -d @- -d grant_type=client_credentials \
      -d client_id=merchant-a-system -d 'scope=consolidation.read' \
      http://127.0.0.1:8080/realms/cashflow/protocol/openid-connect/token \
    | python -I -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')
  sleep 3
  curl -sS $REVOKE --cacert infra/pki/out/ca.crt -w '\nHTTP %{http_code}\n' \
    "https://127.0.0.1:8443/api/v1/merchants/33333333-3333-4333-8333-333333333333/consolidations/daily?date=$D" \
    -H "Authorization: Bearer $TOKEN"
)
echo
echo "## oraculo SQL (transactions_db): linhas do merchant A com a descricao do README (esperado 1, base zerada)"
echo "linhas com a descricao do README: $(docker exec -i cashflow-postgres-1 psql -U migrator -d transactions_db -v ON_ERROR_STOP=1 -At -c "select count(*) from transactions where merchant_id='33333333-3333-4333-8333-333333333333' and description='exemplo do README'" 2>&1)"
echo
$C ps --format 'table {{.Service}}\t{{.State}}\t{{.Status}}' 2>&1
echo "# fim $(date -u +%FT%TZ)"
