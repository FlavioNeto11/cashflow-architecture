#!/usr/bin/env bash
# F7 (T-31, correcao pos-revisao 12a): confere, por API administrativa NAO interativa, que o atributo `merchant_id` do User Profile do realm
# `cashflow` so pode ser editado por admin (permissions.edit == ["admin"]), isto e, o proprio usuario nao altera o seu merchant_id.
# Credenciais do admin vem do .env (carregado por f7-common.sh), vao ao curl por stdin (fora do argv) e NUNCA sao impressas; o token de admin tambem nao.
# Somente leitura (GET). Uso: bash 22-keycloak-profile.sh > 22-keycloak-profile.log 2>&1 ; echo "exit=$?" >> 22-keycloak-profile.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
KCB=http://127.0.0.1:8080
echo "# F7 22 - User Profile do realm cashflow (merchant_id) - $(date -u +%FT%TZ)"
sec "1. token de admin (realm master, client admin-cli, grant password; credencial do .env, nao impressa)"
AT=$(printf 'data = "grant_type=password&client_id=admin-cli"\ndata-urlencode = "username=%s"\ndata-urlencode = "password=%s"\n' "$KEYCLOAK_ADMIN" "$KEYCLOAK_ADMIN_PASSWORD" \
  | curl -sS -K - "$KCB/realms/master/protocol/openid-connect/token" | python -I -c 'import sys,json; d=json.load(sys.stdin); print(d.get("access_token") or "")')
if [ -z "$AT" ]; then echo "PENDENTE: nao foi possivel obter token de admin (ambiente/credencial); nada foi verificado"; exit 3; fi
echo "token de admin obtido (${#AT} chars; nao impresso)"
sec "2. GET /admin/realms/cashflow/users/profile"
printf 'header = "Authorization: Bearer %s"\n' "$AT" | curl -sS -K - -o "$B" -w 'http=%{http_code}\n' "$KCB/admin/realms/cashflow/users/profile"
python -I - "$B" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
attrs = {a["name"]: a for a in d.get("attributes", [])}
print("atributos do User Profile:", sorted(attrs))
print("unmanagedAttributePolicy:", d.get("unmanagedAttributePolicy"))
m = attrs.get("merchant_id")
if not m:
    print("FALHA: atributo merchant_id nao existe no User Profile"); sys.exit(1)
perm = m.get("permissions", {})
print("merchant_id.permissions:", json.dumps(perm, sort_keys=True))
print("merchant_id.required:", json.dumps(m.get("required")), "| validations:", sorted((m.get("validations") or {}).keys()))
ok = perm.get("edit") == ["admin"]
print(("OK" if ok else "FALHA") + ": merchant_id.permissions.edit == [\"admin\"]")
sys.exit(0 if ok else 1)
PY
echo "rc_verificacao=$?"
