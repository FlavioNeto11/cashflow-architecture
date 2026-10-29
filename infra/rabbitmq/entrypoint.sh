#!/usr/bin/env bash
# Entrypoint do broker do MVP. Motivo (verificado em teste, RabbitMQ 4.4): com load_definitions configurado o broker
# NAO cria RABBITMQ_DEFAULT_USER ("Will not seed default virtual host and user: have definitions to load").
# Para manter usuarios e senhas fora de arquivos versionados, este script gera, no boot e somente dentro do container,
# o arquivo de definicoes dos usuarios a partir do ambiente. Ele e importado depois da topologia (ordem alfabetica:
# 10-topology.json, 90-users.json).
#
# F6 (C-20): dois usuarios com finalidades separadas.
#   - Usuario da APLICACAO (RABBITMQ_DEFAULT_USER/PASS; nome mantido porque o Compose base o entrega aos servicos):
#     SEM tag (nao acessa a UI/API de gestao). Permissoes minimas no vhost "/", compartilhadas por transactions-api,
#     consolidation-worker e recon-tool (uniao do que os tres usam; a topologia e declarativa, entao configure e vazio):
#       write  cashflow.transactions (publisher do outbox e redrive) e cashflow.dlx (copia para a DLQ feita pelo worker)
#       read   consolidation.transaction-registered (consumo) e consolidation.dlq (basic.get do redrive)
#   - Usuario de OPERACAO (RABBITMQ_OPS_USER/PASS, opcional): tag administrator, uso humano (UI, rabbitmqadmin, inspecao
#     de DLQ). NAO e entregue a nenhum container de aplicacao. Se as variaveis faltarem, nao e criado (aviso no log).
set -euo pipefail

: "${RABBITMQ_DEFAULT_USER:?RABBITMQ_DEFAULT_USER e obrigatorio}"
: "${RABBITMQ_DEFAULT_PASS:?RABBITMQ_DEFAULT_PASS e obrigatorio}"

# O JSON e escrito sem biblioteca: recusa aspas, barra invertida e caracteres de controle em vez de escapar.
check_value() {
  if [[ "$1" == *'"'* || "$1" == *'\'* || "$1" =~ [[:cntrl:]] ]]; then
    echo >&2 'erro: usuarios/senhas do broker nao podem conter aspas, barra invertida ou caracteres de controle'
    exit 1
  fi
}
check_value "$RABBITMQ_DEFAULT_USER"
check_value "$RABBITMQ_DEFAULT_PASS"

ops_user="${RABBITMQ_OPS_USER:-}"
ops_pass="${RABBITMQ_OPS_PASS:-}"
if [[ -n "$ops_user" || -n "$ops_pass" ]]; then
  : "${ops_user:?RABBITMQ_OPS_USER e RABBITMQ_OPS_PASS devem ser definidos juntos}"
  : "${ops_pass:?RABBITMQ_OPS_USER e RABBITMQ_OPS_PASS devem ser definidos juntos}"
  check_value "$ops_user"
  check_value "$ops_pass"
  if [[ "$ops_user" == "$RABBITMQ_DEFAULT_USER" ]]; then
    echo >&2 'erro: RABBITMQ_OPS_USER deve ser diferente do usuario da aplicacao'
    exit 1
  fi
fi

definitions_dir=/etc/rabbitmq/definitions.d
user_file="$definitions_dir/90-users.json"
mkdir -p "$definitions_dir"
rm -f "$definitions_dir/90-app-user.json" # nome do F4: sobra em volume antigo daria ao usuario da aplicacao a tag antiga
umask 077

# "[.]" em vez de "\." nos padroes: evita escapar barra invertida no JSON.
app_write='^cashflow[.](transactions|dlx)$'
app_read='^consolidation[.](transaction-registered|dlq)$'

users=$(printf '{"name":"%s","password":"%s","tags":[]}' "$RABBITMQ_DEFAULT_USER" "$RABBITMQ_DEFAULT_PASS")
permissions=$(printf '{"user":"%s","vhost":"/","configure":"^$","write":"%s","read":"%s"}' \
  "$RABBITMQ_DEFAULT_USER" "$app_write" "$app_read")
if [[ -n "$ops_user" ]]; then
  users="$users,$(printf '{"name":"%s","password":"%s","tags":["administrator"]}' "$ops_user" "$ops_pass")"
  permissions="$permissions,$(printf '{"user":"%s","vhost":"/","configure":".*","write":".*","read":".*"}' "$ops_user")"
else
  echo >&2 'aviso: RABBITMQ_OPS_USER/PASS ausentes; usuario de operacao (UI/API de gestao) nao sera criado'
fi
printf '{"users":[%s],"permissions":[%s]}\n' "$users" "$permissions" > "$user_file"
chown rabbitmq:rabbitmq "$user_file"

exec docker-entrypoint.sh rabbitmq-server
