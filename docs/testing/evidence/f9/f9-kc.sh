#!/usr/bin/env bash
# F9: clientes TEMPORARIOS no Keycloak local (API administrativa), para os ensaios que precisam de mais tenants ou de um token
# sem a claim merchant_id. Fonte: o client merchant-a-system de infra/keycloak/realm-export.json, com outro clientId, outro valor
# fixo de merchant_id (ou sem o mapper) e segredo aleatorio gerado aqui. Segredo e tokens ficam so em variaveis/stdin: nunca impressos.
# Requer f7-common.sh carregado antes (KEYCLOAK_ADMIN e KEYCLOAK_ADMIN_PASSWORD vem do .env).
KCB=http://127.0.0.1:8080
# kc_admin: token de admin novo a cada chamada (o do admin-cli expira em 60 s); credencial vai ao curl por stdin
kc_admin() {
  printf 'data = "grant_type=password&client_id=admin-cli"\ndata-urlencode = "username=%s"\ndata-urlencode = "password=%s"\n' "$KEYCLOAK_ADMIN" "$KEYCLOAK_ADMIN_PASSWORD" \
    | curl -sS -K - "$KCB/realms/master/protocol/openid-connect/token" | python -I -c 'import sys,json; print(json.load(sys.stdin).get("access_token") or "")'
}
# kc_create <clientId> <merchant_id|-> : cria o client (merchant "-" = sem mapper de merchant_id). Segredo lido de $KC_NEW_SECRET.
kc_create() {
  local at; at=$(kc_admin)
  python -I -X utf8 -c '
import json, os, sys
r = json.load(open("infra/keycloak/realm-export.json", encoding="utf-8"))
c = [x for x in r["clients"] if x["clientId"] == "merchant-a-system"][0]
c = {k: v for k, v in c.items() if k != "id"}
cid, merchant = sys.argv[1], sys.argv[2]
c.update(clientId=cid, name="F9 temporario " + cid, description="Criado e removido pelo roteiro F9 (docs/testing/evidence/f9).", secret=os.environ["KC_NEW_SECRET"])
maps = []
for m in c.get("protocolMappers", []):
    m = {k: v for k, v in m.items() if k != "id"}
    if m["config"].get("claim.name") == "merchant_id":
        if merchant == "-": continue
        m["config"]["claim.value"] = merchant; m["name"] = "merchant_id (fixo: " + merchant + ")"
    maps.append(m)
c["protocolMappers"] = maps
print(json.dumps(c))' "$1" "$2" | curl -sS -o /dev/null -w '%{http_code}' -X POST "$KCB/admin/realms/cashflow/clients" \
    -H "Authorization: Bearer $at" -H 'Content-Type: application/json' --data-binary @-
}
# kc_delete <clientId>: remove o client (codigo HTTP do DELETE; 404 se ja nao existia)
kc_delete() {
  local at id; at=$(kc_admin)
  id=$(curl -sS "$KCB/admin/realms/cashflow/clients?clientId=$1" -H "Authorization: Bearer $at" | python -I -c 'import sys,json; d=json.load(sys.stdin); print(d[0]["id"] if d else "")')
  [ -z "$id" ] && { echo 404; return; }
  curl -sS -o /dev/null -w '%{http_code}' -X DELETE "$KCB/admin/realms/cashflow/clients/$id" -H "Authorization: Bearer $at"
}
kc_secret() { python -I -c 'import secrets; print(secrets.token_urlsafe(32))'; }
