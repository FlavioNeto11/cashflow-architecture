#!/usr/bin/env bash
# F7 19 (C-25/PF-02): valida o MECANISMO de permissoes de chaves de infra/pki/make-certs.sh em CONTAINER Linux (nao e Linux nativo).
# Repo montado somente leitura; saida em volumes Docker descartaveis via PKI_OUT. infra/pki/out NAO e tocado.
# Uso (da raiz do repo): bash docs/testing/evidence/f7/19-make-certs-linux.sh 2>&1 | tee docs/testing/evidence/f7/19-make-certs-linux.log
export MSYS_NO_PATHCONV=1
REPO="$(pwd -W 2>/dev/null || pwd)"
IMG=python:3.12-slim-trixie   # em cache local; traz bash e openssl 3.5
VA=f7-19-pki-a; VB=f7-19-pki-b; VD=f7-19-pki-d
run() { # run <user|-> <volume> [docker args...] -- cmd
  local u="$1" v="$2"; shift 2
  local ua=(); [ "$u" != "-" ] && ua=(--user "$u")
  docker run --rm "${ua[@]}" --entrypoint bash -v "$REPO:/repo:ro" -v "$v:/pki" -e PKI_OUT=/pki "$@"
}
echo "# F7 19 - make-certs.sh em container Linux - $(date -u +%FT%TZ)"
echo "imagem: $IMG | $(docker run --rm --entrypoint sh $IMG -c 'openssl version; uname -sr')"
echo "ESCOPO: container Linux sobre Docker Desktop/WSL2. NAO e host Linux nativo com bind mount real do projeto."
docker volume rm $VA $VB $VD >/dev/null 2>&1
for v in $VA $VB $VD; do docker volume create $v >/dev/null; done
# preparo (root): volume B/D gravavel por uid 1000, para o script rodar e falhar so na etapa de permissao
docker run --rm -u 0 --entrypoint sh -v $VB:/pki -v $VD:/pki2 $IMG -c 'chmod 777 /pki /pki2' && echo "preparo: volumes B e D com raiz 777"

echo; echo "=== (a) root, PKI_OUT=/pki (esperado: exit=0, chaves de servico = 1654:1654 600; ca/foreign-ca/neg-* = 0:0 600)"
run - $VA $IMG -c 'bash /repo/infra/pki/make-certs.sh >/tmp/o 2>&1; rc=$?; echo "exit=$rc"; tail -n 3 /tmp/o | cut -c1-80; stat -c "%u:%g %a %n" /pki/*.key; stat -c "%u:%g %a %n" /pki/ca.crt /pki/blocked-thumbprints.txt'
echo "exit=$? (docker run de a)"

echo; echo "=== (b) uid 1000 sem grupo 1654 (esperado: mensagem com sudo, exit=3, chaves 600 dono 1000, nunca 644)"
run 1000:1000 $VB $IMG -c 'bash /repo/infra/pki/make-certs.sh >/tmp/o 2>/tmp/e; rc=$?; echo "exit=$rc"; cat /tmp/e; stat -c "%u:%g %a %n" /pki/*.key; exit $rc'
echo "exit=$? (propagado do script)"

echo; echo "=== (c1) apos (a): --user 1654:1654 le chave (so codigo de saida e tamanho)"
for k in transactions-api gateway-client; do
  run 1654:1654 $VA $IMG -c "cat /pki/$k.key >/dev/null; echo \"cat exit=\$? bytes=\$(wc -c < /pki/$k.key)\""
done
echo "=== (c2) apos (a): --user 1000:1000 tenta ler (esperado: negado, exit != 0)"
run 1000:1000 $VA $IMG -c 'cat /pki/transactions-api.key >/dev/null 2>/tmp/e; echo "cat exit=$? $(cut -d: -f3 /tmp/e)"'
echo "=== (c3) uid 1000 le cert publico (644): certificados continuam legiveis por todos"
run 1000:1000 $VA $IMG -c 'cat /pki/ca.crt >/dev/null; echo "crt cat exit=$?"'

echo; echo "=== (d) uid 1000 com --group-add 1654 (esperado: exit=0, chaves de servico = 1000:1654 640, ca.key = 1000:1000 600; 1654 le, outro uid 1001 nao)"
run 1000:1000 $VD --group-add 1654 $IMG -c 'bash /repo/infra/pki/make-certs.sh >/tmp/o 2>&1; rc=$?; echo "exit=$rc"; stat -c "%u:%g %a %n" /pki/transactions-api.key /pki/ca.key'
run 1654:1654 $VD $IMG -c 'cat /pki/transactions-api.key >/dev/null; echo "1654 cat exit=$? bytes=$(wc -c < /pki/transactions-api.key)"'
run 1001:1001 $VD $IMG -c 'cat /pki/transactions-api.key >/dev/null 2>&1; echo "1001 cat exit=$?"'

echo; echo "=== limpeza"
docker volume rm $VA $VB $VD >/dev/null && echo "volumes $VA $VB $VD removidos"
docker volume ls --format '{{.Name}}' | grep -c '^f7-19' | sed 's/^/volumes f7-19 restantes: /'

echo; echo "=== Windows/Git Bash: PKI_OUT em scratch fora do repo"
SCR="$(mktemp -d)"; SW="$(cygpath -m "$SCR" 2>/dev/null || echo "$SCR")"
PKI_OUT="$SW" bash infra/pki/make-certs.sh >"$SCR.log" 2>&1; echo "exit=$?"; sed -n '1p' "$SCR.log" | sed "s#$SW#<scratch>#"
echo "arquivos gerados (nomes):"; (cd "$SCR" && ls -1 | tr '\n' ' '); echo
echo "arquivos em infra/pki/out (nomes, referencia):"; (cd infra/pki/out && ls -1 | tr '\n' ' '); echo
diff <(cd "$SCR" && ls -1) <(cd infra/pki/out && ls -1) && echo "MESMO conjunto de nomes que infra/pki/out"
rm -rf "$SCR" "$SCR.log"; [ -e "$SCR" ] && echo "scratch NAO removido" || echo "scratch removido"
