#!/usr/bin/env bash
# F7 (GAP-F5-10): em quais interfaces do host os containers do Testcontainers publicam portas, medido por amostragem de `docker ps`
# (a cada 1 s) enquanto roda `dotnet test --filter ...`. Mede publicacao REAL (0.0.0.0 = toda a rede local; 127.0.0.1 = so loopback).
# Exclui os containers do Compose (nome cashflow-*). Uso: bash 13-fixtures-ports.sh <rotulo> <filtro1> [filtro2 ...]
#   Cada filtro e passado a `dotnet test <csproj> --no-build -c Release --filter <filtro>`; formato do filtro: <csproj>::<expressao>
# Requer build Release previo (dotnet build CashFlow.slnx -c Release).
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"; cd "$REPO" || exit 2
label="$1"; shift
S="$(mktemp -d)"; samples="$S/samples.txt"; : > "$samples"
echo "# F7 13 - portas publicadas pelos fixtures - rotulo=$label - $(date -u +%FT%TZ)"
( while true; do docker ps --format '{{.Image}}|{{.Names}}|{{.Ports}}' 2>/dev/null | grep -v '|cashflow-' >> "$samples"; sleep 1; done ) &
SP=$!
rc=0
for spec in "$@"; do
  proj="${spec%%::*}"; expr="${spec#*::}"
  echo "--- dotnet test $proj --filter \"$expr\""
  dotnet test "$proj" --no-build -c Release --filter "$expr" 2>&1 | grep -E "^(Passed!|Failed!)|Total tests|error" | head -5
  r=${PIPESTATUS[0]}; echo "exit-dotnet-test=$r"; [ "$r" = 0 ] || rc=1
done
kill "$SP" 2>/dev/null; wait "$SP" 2>/dev/null
echo "--- publicacoes observadas (imagem | portas), linhas distintas, containers NAO-Compose:"
sort -u "$samples" | awk -F'|' '{print "  " $1 " | " $3}' | sort -u
echo "--- amostras totais: $(wc -l < "$samples")"
echo "publicacoes em 0.0.0.0 (IPv4) ou [::] (IPv6): $(sort -u "$samples" | awk -F'|' '{print $3}' | grep -oE '(0\.0\.0\.0|\[::\]):[0-9]+->[0-9]+/tcp' | sort -u | wc -l)"
echo "publicacoes em 127.0.0.1: $(sort -u "$samples" | awk -F'|' '{print $3}' | grep -oE '127\.0\.0\.1:[0-9]+->[0-9]+/tcp' | sort -u | wc -l)"
rm -rf "$S"
exit $rc
