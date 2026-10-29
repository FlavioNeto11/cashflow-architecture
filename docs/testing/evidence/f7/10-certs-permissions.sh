#!/usr/bin/env bash
# F7 (item C-25): permissoes das chaves privadas montadas nos containers + prova do MECANISMO que quebraria num Docker Linux nativo.
# HONESTIDADE: este ambiente e Docker Desktop (Windows, WSL2/VM). A stack NAO foi validada em engine Linux nativo; aqui se mede
# (a) o dono/modo/uid vistos DENTRO dos containers e (b) em volume Linux efemero, que uma chave 600 de outro uid nao e legivel pelo
# uid do app. (b) demonstra a premissa, nao e o bind mount real do projeto.
# Uso: bash 10-certs-permissions.sh > 10-certs-permissions.log 2>&1 ; echo "exit=$?" >> 10-certs-permissions.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
echo "# F7 10 - permissoes das chaves (C-25) - $(date -u +%FT%TZ)"
echo "docker: $(docker version --format 'cliente={{.Client.Version}} servidor={{.Server.Version}} os/arch={{.Server.Os}}/{{.Server.Arch}}') | $(docker info --format 'nome={{.Name}} kernel={{.KernelVersion}} so={{.OperatingSystem}}')"
echo "host: arquivos em infra/pki/out (ls -l do Git Bash; NTFS nao reflete o modo POSIX gravado por make-certs.sh: chmod 600 *.key):"
ls -l infra/pki/out/*.key | awk '{print "  " $1, $3, $4, $NF}'
for c in cashflow-gateway-1 cashflow-transactions-api-1 cashflow-consolidation-api-1; do
  echo; echo "=== $c"
  echo "Config.User='$(docker inspect -f '{{.Config.User}}' $c)' | imagem: $(docker inspect -f '{{.Config.Image}}' $c)"
  echo "processo principal (docker top): $(docker top $c -eo user,pid,comm 2>&1 | tail -n +2 | head -3 | tr '\n' ';')"
  if docker exec $c sh -c 'true' >/dev/null 2>&1; then
    docker exec $c sh -c 'id; ls -ln /certs; for f in /certs/*.key; do [ -e "$f" ] && { test -r "$f" && echo "legivel pelo uid atual: $f" || echo "NAO legivel pelo uid atual: $f"; }; done' 2>&1 | sed 's/^/  /'
  else
    echo "  (a imagem nao tem shell: docker exec sh indisponivel; usando docker run --volumes-from nao e possivel sem shell)"
    docker exec $c /certs 2>&1 | head -1 | sed 's/^/  /'
  fi
done
echo; echo "=== Mecanismo (volume Linux efemero f7-c25-test, apagado ao fim): chave 600 de dono uid 1000 lida por uid 1654 (usuario padrao app das imagens .NET)"
MSYS_NO_PATHCONV=1 docker run --rm -u 0 --entrypoint sh -v f7-c25-test:/v "$CURL_IMG" -c 'echo segredo-de-teste > /v/k.key && chown 1000:1000 /v/k.key && chmod 600 /v/k.key && ls -ln /v/k.key' 2>&1 | sed 's/^/  criar (root): /'
MSYS_NO_PATHCONV=1 docker run --rm -u 1654 --entrypoint sh -v f7-c25-test:/v "$CURL_IMG" -c 'id; cat /v/k.key >/dev/null && echo LEU || echo NEGADO' 2>&1 | sed 's/^/  uid 1654 le (600, dono 1000): /'
MSYS_NO_PATHCONV=1 docker run --rm -u 0 --entrypoint sh -v f7-c25-test:/v "$CURL_IMG" -c 'chown 1654 /v/k.key && ls -ln /v/k.key' 2>&1 | sed 's/^/  correcao A (chown 1654): /'
MSYS_NO_PATHCONV=1 docker run --rm -u 1654 --entrypoint sh -v f7-c25-test:/v "$CURL_IMG" -c 'cat /v/k.key >/dev/null && echo LEU || echo NEGADO' 2>&1 | sed 's/^/  uid 1654 le apos chown 1654: /'
MSYS_NO_PATHCONV=1 docker run --rm -u 0 --entrypoint sh -v f7-c25-test:/v "$CURL_IMG" -c 'chown 1000:1654 /v/k.key && chmod 640 /v/k.key && ls -ln /v/k.key' 2>&1 | sed 's/^/  correcao B (640, grupo 1654): /'
MSYS_NO_PATHCONV=1 docker run --rm -u 1654:1654 --entrypoint sh -v f7-c25-test:/v "$CURL_IMG" -c 'id; cat /v/k.key >/dev/null && echo LEU || echo NEGADO' 2>&1 | sed 's/^/  uid:gid 1654:1654 le apos 640 e grupo 1654: /'
docker volume rm f7-c25-test >/dev/null 2>&1 && echo "volume efemero f7-c25-test removido"
echo "LIMITE: a leitura no bind mount de Docker Desktop depende do driver de arquivos (virtiofs/9p), que expoe o modo de forma diferente de um host Linux; so um host Linux nativo valida."
