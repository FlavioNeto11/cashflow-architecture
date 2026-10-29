#!/usr/bin/env bash
# F7 (pos-revisao): varredura de vulnerabilidades com trivy, REPRODUTIVEL, das imagens construidas (cashflow/*:dev) e das de terceiros do
# Compose BASE (postgres, rabbitmq, keycloak). Cada imagem e exportada com `docker save` para um diretorio temporario, varrida num container
# efemero do trivy (imagem fixada por digest), e o JSON e resumido por 12-trivy-summarize.py (versao do trivy, metadados da base de
# vulnerabilidades com UpdatedAt/DownloadedAt, ID e RepoDigests de cada imagem, contagens por severidade e alvo, lista de CVE).
# Requer rede (baixa a base de vulnerabilidades). Nao altera a stack. Imagens `cashflow/*:dev` sao as do momento da execucao: o ID e
# registrado; reexecutar apos rebuild muda o resultado.
# Uso: bash 12-trivy-imagens.sh > 12-trivy-imagens.log 2>&1 ; echo "exit=$?" >> 12-trivy-imagens.log
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
D="$REPO/docs/testing/evidence/f7"
TRIVY_IMG='aquasec/trivy@sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c'
IMAGES=(cashflow/gateway:dev cashflow/transactions-api:dev cashflow/consolidation-api:dev cashflow/consolidation-worker:dev cashflow/recon-tool:dev
        postgres:17 rabbitmq:4-management quay.io/keycloak/keycloak:26.8.1)
mkdir -p "$S/in" "$S/out"
echo "# F7 12 - trivy sobre as imagens do Compose BASE - $(date -u +%FT%TZ)"
echo "imagem do trivy: $TRIVY_IMG"
echo "referencias fixadas no Compose (grep):"; grep -nE '^\s*image: (postgres|rabbitmq|quay)' infra/compose/docker-compose.yml | sort -u -k3 | sed 's/^/  /'
i=0; : > "$S/list.txt"
for img in "${IMAGES[@]}"; do
  i=$((i+1)); n=$(printf '%02d' $i)
  echo "[$(ts)] docker save $img  (id=$(docker image inspect -f '{{.Id}}' "$img" 2>/dev/null | cut -c1-19))"
  docker save "$img" -o "$S/in/$n.tar" || { echo "FALHA: docker save $img"; exit 1; }
  echo "$n $img" >> "$S/list.txt"
done
cp "$S/list.txt" "$S/out/list.txt"
echo "[$(ts)] trivy em container efemero (baixa a base; varre ${#IMAGES[@]} imagens)"
MSYS_NO_PATHCONV=1 docker run --rm --entrypoint sh -v "$S/in:/in:ro" -v "$S/out:/out" "$TRIVY_IMG" -c '
set -e
trivy image --download-db-only --quiet 2>/dev/null || trivy image --download-db-only
trivy version --format json > /out/trivy-version.json
while read -r n img; do
  echo "  varrendo $img"
  trivy image --input "/in/$n.tar" --scanners vuln --format json --quiet --output "/out/$n.json" || echo "  FALHA trivy em $img"
done < /out/list.txt
' 2>&1 | grep -v 'libgssapi' | sed 's/^/  /'
echo "[$(ts)] resumo"
for img in "${IMAGES[@]}"; do echo "  RepoDigests de $img: $(docker image inspect -f '{{json .RepoDigests}}' "$img")"; done
python -I "$D/12-trivy-summarize.py" "$S/out" "$D/12-trivy-imagens-resumo.json"
