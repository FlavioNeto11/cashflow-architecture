#!/usr/bin/env bash
# PKI LOCAL DE DEMONSTRACAO (F5, security.md 6; ADR-0011). Gera em infra/pki/out/ (ignorado pelo Git):
#   ca.crt/ca.key                      CA raiz local (so esta maquina)
#   transactions-api.crt/.key          servidor, SAN DNS:transactions-api,localhost IP:127.0.0.1, EKU serverAuth
#   consolidation-api.crt/.key         servidor, SAN DNS:consolidation-api,localhost IP:127.0.0.1, EKU serverAuth
#   gateway-client.crt/.key            cliente mTLS do gateway, SAN DNS:gateway.internal, EKU clientAuth
#   gateway-edge.crt/.key              TLS da borda do gateway, SAN DNS:localhost,gateway IP:127.0.0.1, EKU serverAuth
#   neg-foreign-ca.crt/.key            NEGATIVO: cliente valido emitido por OUTRA CA (foreign-ca.crt)
#   neg-expired.crt/.key               NEGATIVO: cliente da CA local, ja expirado
#   neg-wrong-san.crt/.key             NEGATIVO: cliente da CA local, SAN DNS:intruso.internal
#   neg-wrong-eku.crt/.key             NEGATIVO: cliente da CA local, SAN correto mas EKU serverAuth (sem clientAuth)
#   blocked-thumbprints.txt            blocklist vazia (formato: um thumbprint SHA-1 hex por linha; '#' comenta)
# NAO e uma PKI de producao: sem CRL/OCSP, chaves em disco sem senha (modo 600/640; ver politica por ambiente no fim),
# validade curta. blocked-thumbprints.txt e montado como ARQUIVO nos containers: edite-o no lugar (>> ou editor que
# grava no mesmo inode); substituir o arquivo por outro inode deixa o container vendo o antigo. Em producao os certificados vem do PKI corporativo/mesh (security.md 6, ALVO).
# Uso: bash infra/pki/make-certs.sh [--force]   (requer openssl >= 3.4; testado com 3.5)
# Variaveis: PKI_OUT (saida; padrao infra/pki/out), PKI_KEY_UID / PKI_KEY_GID (uid/gid do usuario app dos containers; padrao 1654,
#   usado so em host Linux para tornar as chaves legiveis pelo container; ver bloco de permissoes no fim).
set -euo pipefail
export MSYS_NO_PATHCONV=1 # Git Bash: nao converter "/CN=..." em caminho

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="${PKI_OUT:-$HERE/out}"
KEY_UID="${PKI_KEY_UID:-1654}"
KEY_GID="${PKI_KEY_GID:-1654}"
# Git Bash: o openssl e um binario Windows e, com MSYS_NO_PATHCONV=1, nao recebe mais "/c/..." convertido; passe "C:/...".
command -v cygpath >/dev/null 2>&1 && OUT="$(cygpath -m "$OUT")"
DAYS_CA=825
DAYS_LEAF=180

if [ -d "$OUT" ] && [ "${1:-}" != "--force" ] && [ -f "$OUT/ca.crt" ]; then
  echo "ja existe: $OUT (use --force para regenerar; os servicos precisam reiniciar)"; exit 0
fi
mkdir -p "$OUT"
umask 022

ver="$(openssl version | awk '{print $2}')"
case "$ver" in 3.[4-9]*|3.[1-9][0-9]*|[4-9]*) ;; *) echo "openssl >= 3.4 necessario (encontrado $ver)"; exit 1;; esac

ca() { # ca <nome> <CN>
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -sha256 -days "$DAYS_CA" \
    -subj "/CN=$2" -addext "basicConstraints=critical,CA:TRUE,pathlen:0" -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -keyout "$OUT/$1.key" -out "$OUT/$1.crt" 2>/dev/null
}
leaf() { # leaf <nome> <CN> <SAN> <EKU> <ca> [extra x509 args...]
  local name="$1" cn="$2" san="$3" eku="$4" ca="$5"; shift 5
  openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -sha256 -subj "/CN=$cn" \
    -addext "subjectAltName=$san" -addext "extendedKeyUsage=$eku" -addext "keyUsage=critical,digitalSignature" \
    -addext "basicConstraints=CA:FALSE" -keyout "$OUT/$name.key" -out "$OUT/$name.csr" 2>/dev/null
  openssl x509 -req -in "$OUT/$name.csr" -CA "$OUT/$ca.crt" -CAkey "$OUT/$ca.key" -CAcreateserial -sha256 \
    -copy_extensions copy -days "$DAYS_LEAF" "$@" -out "$OUT/$name.crt" 2>/dev/null
  rm -f "$OUT/$name.csr"
}

ca ca "CashFlow Local CA (demo)"
ca foreign-ca "Outra CA (negativo)"
leaf transactions-api  transactions-api  "DNS:transactions-api,DNS:localhost,IP:127.0.0.1"  serverAuth ca
leaf consolidation-api consolidation-api "DNS:consolidation-api,DNS:localhost,IP:127.0.0.1" serverAuth ca
leaf gateway-client    gateway.internal  "DNS:gateway.internal"                              clientAuth ca
leaf gateway-edge      gateway           "DNS:localhost,DNS:gateway,IP:127.0.0.1"            serverAuth ca
leaf neg-foreign-ca    gateway.internal  "DNS:gateway.internal"                              clientAuth foreign-ca
leaf neg-wrong-san     intruso.internal  "DNS:intruso.internal"                              clientAuth ca
leaf neg-wrong-eku     gateway.internal  "DNS:gateway.internal"                              serverAuth ca
leaf neg-expired       gateway.internal  "DNS:gateway.internal"                              clientAuth ca \
  -not_before "$(date -u -d '-30 days' +%Y%m%d%H%M%SZ 2>/dev/null || date -u -v-30d +%Y%m%d%H%M%SZ)" \
  -not_after  "$(date -u -d '-1 day'   +%Y%m%d%H%M%SZ 2>/dev/null || date -u -v-1d  +%Y%m%d%H%M%SZ)"

[ -f "$OUT/blocked-thumbprints.txt" ] || printf '# thumbprints SHA-1 (hex) de certificados de cliente recusados; relido pela API quando o arquivo muda\n' > "$OUT/blocked-thumbprints.txt"
rm -f "$OUT"/*.srl
# Chaves privadas NUNCA ficam 644. Politica por ambiente (auditoria F5 P1-1; F7 PF-02/C-25; security.md 6):
# - Docker Desktop (Windows/MSYS/MINGW, macOS/Darwin): o bind mount apresenta os arquivos como root 777 no container, o modo do
#   host nao e aplicado -> o uid 1654 le a chave; mantem-se chmod 600 (dono = quem gerou). Validado so neste cenario.
# - Host Linux: o bind mount preserva dono e modo; 600 com dono = usuario do host deixaria o uid 1654 sem leitura (servico nao
#   sobe). Entao: (1) root -> chown UID:GID + chmod 600; (2) usuario pertence ao grupo GID -> chgrp GID + chmod 640;
#   (3) dono ja e o UID -> chmod 600; (4) nenhum dos casos -> NAO afrouxa, imprime o comando com sudo e sai com codigo 3.
# - Producao: segredos do orquestrador/PKI gerenciada, nao este script.
fix_key_perms() {
  # So as chaves montadas em containers (README infra 29-certs-file-mounts) precisam ser legiveis pelo uid/gid do app;
  # ca.key, foreign-ca.key e neg-*.key nunca entram em container e ficam 600 do dono (minimo privilegio).
  local keys=() me gid_ok=0 g n
  for n in transactions-api consolidation-api gateway-client gateway-edge; do keys+=("$OUT/$n.key"); done
  chmod 600 "$OUT"/*.key
  me="$(id -u)"
  case "$(uname -s)" in
    Linux) ;;
    *) chmod 600 "${keys[@]}"; return 0;;
  esac
  for g in $(id -G); do [ "$g" = "$KEY_GID" ] && gid_ok=1; done
  if [ "$me" = 0 ]; then
    chown "$KEY_UID:$KEY_GID" "${keys[@]}" && chmod 600 "${keys[@]}"
  elif [ "$me" = "$KEY_UID" ]; then
    chmod 600 "${keys[@]}"
  elif [ "$gid_ok" = 1 ]; then
    chgrp "$KEY_GID" "${keys[@]}" && chmod 640 "${keys[@]}"
  else
    chmod 600 "${keys[@]}" # permanece restrito (fail-closed)
    {
      echo "ERRO: as chaves em $OUT ficaram 600 e dono uid $me; o uid $KEY_UID dos containers NAO as le (servicos nao subiriam)."
      echo "      Este usuario nao e root nem pertence ao grupo $KEY_GID. Nao foi afrouxado para 644. Rode:"
      echo "        sudo chown $KEY_UID:$KEY_GID \"$OUT\"/*.key && sudo chmod 600 \"$OUT\"/*.key"
      echo "      (ou rode este script com sudo, ou como usuario do grupo $KEY_GID)."
    } >&2
    return 3
  fi
}
fix_key_perms || { rc=$?; echo "falha ao ajustar permissoes das chaves (codigo $rc)" >&2; exit "$rc"; }
chmod 644 "$OUT"/*.crt "$OUT/blocked-thumbprints.txt"

echo "gerado em $OUT:"
for c in "$OUT"/*.crt; do
  printf '  %-22s %s | %s\n' "$(basename "$c")" "$(openssl x509 -in "$c" -noout -subject -ext subjectAltName 2>/dev/null | tr '\n' ' ' | sed 's/  */ /g')" "$(openssl x509 -in "$c" -noout -enddate)"
done
