#!/usr/bin/env bash
# F7 (T-07, T-17 parcial, T-25, T-26, T-28): bateria de seguranca/tenant pelo gateway REAL (Compose BASE, JWT real do Keycloak).
# Nao altera a stack; cria poucos lancamentos de teste (merchants A/B fictícios) em datas proprias. Nenhum token e impresso.
# Uso: bash docs/testing/evidence/f7/02-security-scenarios.sh > docs/testing/evidence/f7/02-security-scenarios.log 2>&1
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
FAIL=0
expect() { # expect <rotulo> <esperado> <obtido>
  if [ "$2" = "$3" ]; then echo "  [OK]    $1 (esperado $2, obtido $3)"; else echo "  [FALHA] $1 (esperado $2, obtido $3)"; FAIL=$((FAIL+1)); fi
}
b64u() { base64 -w0 | tr '+/' '-_' | tr -d '='; }
echo "# F7 02 - seguranca/tenant pelo gateway - $(date -u +%FT%TZ)"
sec "0. tokens (A com 3 escopos, A so write, B)"
mktokens
echo "token A: ${#TA} chars"; claims "$TA"
echo "token A (so write): ${#TAW} chars"; echo "token B: ${#TB} chars"; claims "$TB" | grep merchant
D=${D:-2026-03-17}   # data propria do roteiro (isola da massa de outros roteiros); sobrescrevivel para reexecucao

sec "1. Autenticacao (T-07/T-26): 401 e cabecalho WWW-Authenticate"
expect "sem token" 401 "$(code -X POST "$GW/merchants/$A/transactions" -H 'Idempotency-Key: s1' -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00"}')"
expect "token adulterado (ultimo caractere trocado)" 401 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer ${TA%?}x")"
NONE="$(printf '{"alg":"none","typ":"JWT"}' | b64u).$(echo "$TA" | cut -d. -f2)."
expect "alg=none" 401 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $NONE")"
HS="$(printf '{"alg":"HS256","typ":"JWT"}' | b64u).$(echo "$TA" | cut -d. -f2)"
SIG=$(printf '%s' "$HS" | openssl dgst -sha256 -hmac "$(cat "$CA")" -binary | b64u)
expect "HS256 assinado com a chave publica/CA como segredo (confusao de algoritmo)" 401 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $HS.$SIG")"
KID="$(printf '{"alg":"RS256","typ":"JWT","kid":"desconhecido-f7"}' | b64u).$(echo "$TA" | cut -d. -f2).$(echo "$TA" | cut -d. -f3)"
expect "RS256 com kid desconhecido" 401 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $KID")"
expect "esquema errado (Basic em vez de Bearer)" 401 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Basic $(printf 'a:b' | base64)")"
expect "token vazio" 401 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer ")"
req "sem token: cabecalhos de 401" "$GW/merchants/$A/consolidations/daily?date=$D"
echo "--- rajada de 40 tokens com kid desconhecido (T-26, so codigos; a contagem de buscas ao JWKS no Keycloak NAO foi medida)"
for i in $(seq 1 40); do code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $KID"; echo; done | sort | uniq -c | sed 's/^/  /'
expect "token valido continua aceito apos a rajada" 200 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TA")"

sec "2. Autorizacao por escopo (403) - ADR-0006"
expect "token so write no consolidado" 403 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TAW")"
expect "token so write na listagem (transactions.read)" 403 "$(code "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TAW")"
req "403: cabecalhos e corpo" "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TAW"

sec "3. Isolamento de tenant (T-07/T-25): rota != claim => 404 identico; nenhuma linha/evento criado"
N0=$($PSQL_T -c "SELECT count(*) FROM transactions"); I0=$($PSQL_T -c "SELECT count(*) FROM idempotency_keys"); O0=$($PSQL_T -c "SELECT count(*) FROM outbox_messages")
expect "A faz POST na rota de B" 404 "$(post "$BM" "$TA" "f7-xt-$RANDOM$RANDOM" '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'"}')"
cp "$B" "$S/xt_post.json"
expect "A le consolidado de B" 404 "$(code "$GW/merchants/$BM/consolidations/daily?date=$D" -H "Authorization: Bearer $TA")"
curl -sS $REVOKE --cacert "$CA" -o "$S/xt_get.json" "$GW/merchants/$BM/consolidations/daily?date=$D" -H "Authorization: Bearer $TA"
expect "A lista transacoes de B" 404 "$(code "$GW/merchants/$BM/transactions" -H "Authorization: Bearer $TA")"
# cria um lancamento de B e tenta le-lo/estorna-lo como A pela rota de A e pela de B
KB="f7-b-$RANDOM$RANDOM"; expect "B cria seu lancamento" 201 "$(post "$BM" "$TB" "$KB" '{"type":"CREDIT","amount":"7.00","effectiveDate":"'$D'"}')"
TXB=$(jfield id < "$B")
expect "A le lancamento de B pela rota de A" 404 "$(code "$GW/merchants/$A/transactions/$TXB" -H "Authorization: Bearer $TA")"
expect "A le lancamento de B pela rota de B" 404 "$(code "$GW/merchants/$BM/transactions/$TXB" -H "Authorization: Bearer $TA")"
expect "A estorna lancamento de B pela rota de A" 404 "$(curl -sS $REVOKE --cacert "$CA" -o /dev/null -w '%{http_code}' -X POST "$GW/merchants/$A/transactions/$TXB/reversal" -H "Authorization: Bearer $TA" -H "Idempotency-Key: f7-rev-$RANDOM" -H 'Content-Type: application/json' -d '{}')"
expect "A estorna lancamento de B pela rota de B" 404 "$(curl -sS $REVOKE --cacert "$CA" -o /dev/null -w '%{http_code}' -X POST "$GW/merchants/$BM/transactions/$TXB/reversal" -H "Authorization: Bearer $TA" -H "Idempotency-Key: f7-rev-$RANDOM" -H 'Content-Type: application/json' -d '{}')"
expect "A usa a mesma Idempotency-Key de B na rota de A (nao deve devolver o replay de B)" 201 "$(post "$A" "$TA" "$KB" '{"type":"CREDIT","amount":"3.00","effectiveDate":"'$D'"}')"
echo "  corpo do 201 de A (merchantId deve ser A): merchantId=$(jfield merchantId < "$B")"
expect "merchantId do 201 de A e A" "$A" "$(jfield merchantId < "$B")"
echo "--- 404 cross-tenant x 404 recurso inexistente do proprio A: corpos (ADR-0006 exige indistinguiveis)"
curl -sS $REVOKE --cacert "$CA" -o "$S/own404.json" "$GW/merchants/$A/transactions/00000000-0000-4000-8000-000000000000" -H "Authorization: Bearer $TA"
echo "  cross-tenant GET : $(cat "$S/xt_get.json" | tr -d '\n' | head -c 300)"
echo "  inexistente (A)  : $(cat "$S/own404.json" | tr -d '\n' | head -c 300)"
python -I - "$S/xt_get.json" "$S/own404.json" <<'PY'
import json,sys
a,b=[json.load(open(p)) for p in sys.argv[1:3]]
strip=lambda d:{k:v for k,v in d.items() if k not in ("traceId","instance","detail")}
print("  comparacao (sem traceId/instance/detail):", "IGUAIS" if strip(a)==strip(b) else "DIFERENTES")
print("  campos comparados:", sorted(strip(a)), "| detail cross:", a.get("detail"), "| detail proprio:", b.get("detail"))
PY
N1=$($PSQL_T -c "SELECT count(*) FROM transactions"); I1=$($PSQL_T -c "SELECT count(*) FROM idempotency_keys"); O1=$($PSQL_T -c "SELECT count(*) FROM outbox_messages")
echo "  deltas apos a secao: transactions +$((N1-N0)) (esperado +2: B 7.00 e A 3.00), idempotency_keys +$((I1-I0)) (esperado +2), outbox +$((O1-O0)) (esperado +2)"
expect "delta de transactions" 2 $((N1-N0)); expect "delta de idempotency_keys" 2 $((I1-I0)); expect "delta de outbox" 2 $((O1-O0))

sec "4. merchant_id imutavel / derivado do token (ADR-0006)"
echo "  Os clientes de servico do realm usam mapper HARDCODED (claim fixa por cliente): nao ha como o cliente alterar a claim; o"
echo "  atributo de usuario merchant_id tem permissions.edit=[admin] no user profile (declarado no realm; edicao por usuario NAO exercitada: sem fluxo de senha/navegador)."
expect "corpo com membro 'merchantId' (membro desconhecido => 400)" 400 "$(post "$A" "$TA" "f7-mb-$RANDOM" '{"type":"CREDIT","amount":"1.00","merchantId":"'$BM'"}')"
expect "corpo com membro 'merchant_id' (idem)" 400 "$(post "$A" "$TA" "f7-mb2-$RANDOM" '{"type":"CREDIT","amount":"1.00","merchant_id":"'$BM'"}')"
expect "query ?merchantId=B na rota de A e ignorada (GET consolidado de A devolve A)" 200 "$(code "$GW/merchants/$A/consolidations/daily?date=$D&merchantId=$BM" -H "Authorization: Bearer $TA")"
echo "  merchantId devolvido com ?merchantId=B: $(daily "$A" "$TA" "$D" "&merchantId=$BM" | jfield merchantId)"
expect "?merchantId=B nao altera o tenant do consolidado" "$A" "$(daily "$A" "$TA" "$D" "&merchantId=$BM" | jfield merchantId)"

sec "5. Cabecalhos X-Dev-* (modo de desenvolvimento) no gateway"
expect "X-Dev-* sem token" 401 "$(code -X POST "$GW/merchants/$A/transactions" -H "X-Dev-Sub: intruso" -H "X-Dev-Client-Id: x" -H "X-Dev-Merchant-Id: $A" -H "X-Dev-Scope: transactions.write" -H 'Idempotency-Key: d1' -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00"}')"
echo "  token A + X-Dev-Merchant-Id=B + X-Dev-Scope extra, rota de B: o token governa => 404"
expect "token A + X-Dev-* de B na rota de B" 404 "$(code -X POST "$GW/merchants/$BM/transactions" -H "Authorization: Bearer $TA" -H "X-Dev-Sub: x" -H "X-Dev-Client-Id: x" -H "X-Dev-Merchant-Id: $BM" -H "X-Dev-Scope: transactions.write" -H 'Idempotency-Key: d2' -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00"}')"
expect "token so-write + X-Dev-Scope com consolidation.read no consolidado (nao eleva)" 403 "$(code "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TAW" -H "X-Dev-Scope: consolidation.read" -H "X-Dev-Merchant-Id: $A" -H "X-Dev-Sub: x" -H "X-Dev-Client-Id: x")"

sec "6. Limite de corpo e rotas nao expostas"
head -c 17408 /dev/zero | tr '\0' 'x' > "$S/big.txt"
expect "corpo 17 KB com token valido" 413 "$(code -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TA" -H 'Idempotency-Key: big1' -H 'Content-Type: application/json' --data-binary "@$S/big.txt")"
expect "corpo 17 KB sem token" 413 "$(code -X POST "$GW/merchants/$A/transactions" -H 'Content-Type: application/json' --data-binary "@$S/big.txt")"
expect "/health/ready pelo gateway publicado (nao roteado)" 404 "$(code "$GWH/health/ready" -H "Authorization: Bearer $TA")"
expect "/metrics pelo gateway publicado" 404 "$(code "$GWH/metrics" -H "Authorization: Bearer $TA")"
expect "HTTP puro na porta TLS de borda" 000 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:8443/api/v1 2>/dev/null || true)"

sec "7. Cabecalhos de seguranca (EdgeMiddleware.cs) e ausencia de Server em 2xx, 4xx e 5xx (o 502 do gateway e coberto no roteiro 07)"
hdr_assert() { # hdr_assert <rotulo> <status esperado>: confere status e cabecalhos de $H (ultima req)
  expect "$1: status" "$2" "$(head -1 "$H" | tr -d '\r' | awk '{print $2}')"
  expect "$1: Server ausente" 0 "$(grep -ci '^server:' "$H")"
  expect "$1: X-Content-Type-Options nosniff" 1 "$(grep -ciE '^x-content-type-options: *nosniff' "$H")"
  expect "$1: Cache-Control no-store" 1 "$(grep -ciE '^cache-control:.*no-store' "$H")"
  expect "$1: Referrer-Policy no-referrer" 1 "$(grep -ciE '^referrer-policy: *no-referrer' "$H")"
}
req "200 consolidado" "$GW/merchants/$A/consolidations/daily?date=$D" -H "Authorization: Bearer $TA"; hdr_assert "200 consolidado" 200
req "201 POST" -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TA" -H "Idempotency-Key: f7-hdr-$RANDOM" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"0.01","effectiveDate":"'$D'"}'; hdr_assert "201 POST" 201
req "404 rota inexistente do gateway" "$GW/rota-inexistente" -H "Authorization: Bearer $TA"; hdr_assert "404 gateway" 404
req "422 data invalida" "$GW/merchants/$A/consolidations/daily?date=2026-13-45" -H "Authorization: Bearer $TA"; hdr_assert "422 da API" 422
req "401" "$GW/merchants/$A/consolidations/daily?date=$D"; hdr_assert "401 do gateway" 401
# Na execucao registrada em f7/02-security-scenarios.log, description com NUL provocava 500 (PF-03) e servia para conferir cabecalhos em 5xx gerado pela API.
# PF-03 (422) e PF-17 (UTF-8 invalido -> 400) foram corrigidos: a API nao tem mais 5xx provocavel pelo cliente. A verificacao passa a ser regressao de PF-03 (422);
# cabecalhos em 5xx ficam cobertos pelo 502 do gateway com consolidation-api parada (roteiro 07, R1).
req "422 NUL em description (regressao PF-03)" -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TAW" -H "Idempotency-Key: f7-hdr5xx-$RANDOM-$RANDOM" -H 'Content-Type: application/json' -d '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'","description":"x\u0000y"}'; hdr_assert "422 NUL em description" 422

sec "8. 404 do gateway x 404 da API (item 'F7 dados' de decisions-needed): corpos"
echo "--- gateway, rota inexistente (sem token e com token)"
curl -sS $REVOKE --cacert "$CA" -D - -o "$S/g404.json" "$GW/rota-inexistente" | tr -d '\r' | head -1; echo "  sem token: $(head -c 300 "$S/g404.json")"
curl -sS $REVOKE --cacert "$CA" -o "$S/g404t.json" "$GW/rota-inexistente" -H "Authorization: Bearer $TA"; echo "  com token: $(head -c 300 "$S/g404t.json")"
echo "--- API direta (mTLS com o certificado do gateway, a partir da rede app), rota inexistente"
MSYS_NO_PATHCONV=1 docker run --rm --network cashflow_app -v "$PKI_WIN/ca.crt:/certs/ca.crt:ro" -v "$PKI_WIN/gateway-client.crt:/certs/gateway-client.crt:ro" -v "$PKI_WIN/gateway-client.key:/certs/gateway-client.key:ro" "$CURL_IMG" -sS --max-time 15 --cacert /certs/ca.crt --cert /certs/gateway-client.crt --key /certs/gateway-client.key -H "Authorization: Bearer $TA" -w '\n  http=%{http_code}\n' https://transactions-api:8080/api/v1/rota-inexistente 2>&1 | sed 's/^/  API: /' | head -5

sec "9. API direta na rede app (T-17/T-28): 8080 exige mTLS; 8081 so /health/*"
# Montagens POR ARQUIVO (C-25): so ca.crt; o par do gateway-client apenas onde o cenario o exige; a CA estranha (negativo) so no cenario dela.
CA_M=(-v "$PKI_WIN/ca.crt:/certs/ca.crt:ro")
GWC_M=(-v "$PKI_WIN/gateway-client.crt:/certs/gateway-client.crt:ro" -v "$PKI_WIN/gateway-client.key:/certs/gateway-client.key:ro")
FOR_M=(-v "$PKI_WIN/neg-foreign-ca.crt:/certs/neg-foreign-ca.crt:ro" -v "$PKI_WIN/neg-foreign-ca.key:/certs/neg-foreign-ca.key:ro")
DMOUNT=("${CA_M[@]}")
# direct <rotulo> <regex dos codigos de saida do curl aceitos> <http esperado> <args do curl...>  (usa DMOUNT)
direct() { local label="$1" rcre="$2" want="$3"; shift 3
  local out rc http; out=$(MSYS_NO_PATHCONV=1 docker run --rm --network cashflow_app "${DMOUNT[@]}" "$CURL_IMG" -sS -v --max-time 15 -o /dev/null -w 'http=%{http_code}' "$@" 2>&1); rc=$?
  http=$(echo "$out" | grep -oE 'http=[0-9]+' | tail -1 | cut -d= -f2)
  local detail; detail=$(echo "$out" | grep -iE 'alert|certificate required|handshake failure|bad certificate|unknown ca|refused|Empty reply|reset' | head -1 | cut -c1-120)
  if [[ "$rc" =~ ^($rcre)$ ]] && [ "$http" = "$want" ]; then echo "  [OK]    $label (curl exit=$rc, http=$http; esperado exit em ($rcre) e http=$want) $detail"
  else echo "  [FALHA] $label (curl exit=$rc, http=$http; esperado exit em ($rcre) e http=$want) $detail"; FAIL=$((FAIL+1)); fi; }
for svc in transactions-api consolidation-api; do
  DMOUNT=("${CA_M[@]}")
  direct "$svc:8080 sem certificado de cliente recusado" '35|56' 000 --cacert /certs/ca.crt "https://$svc:8080/api/v1"
  DMOUNT=("${CA_M[@]}" "${FOR_M[@]}")
  direct "$svc:8080 com certificado de CA estranha recusado" '35|56' 000 --cacert /certs/ca.crt --cert /certs/neg-foreign-ca.crt --key /certs/neg-foreign-ca.key "https://$svc:8080/api/v1"
  DMOUNT=("${CA_M[@]}" "${GWC_M[@]}")
  direct "$svc:8080 CONTROLE POSITIVO com gateway-client (chega a aplicacao: 404 de rota inexistente)" '0' 404 --cacert /certs/ca.crt --cert /certs/gateway-client.crt --key /certs/gateway-client.key -H "Authorization: Bearer $TA" "https://$svc:8080/api/v1/rota-inexistente"
  DMOUNT=("${CA_M[@]}")
  direct "$svc:8080 HTTP puro recusado (resposta vazia ou reset)" '52|56' 000 "http://$svc:8080/api/v1"
  direct "$svc:8081 /health/ready (porta de saude, texto claro, por desenho)" '0' 200 "http://$svc:8081/health/ready"
  direct "$svc:8081 rota de negocio nao existe" '0' 404 "http://$svc:8081/api/v1/merchants/$A/transactions"
  direct "$svc:8081 /health/live/ (barra final)" '0' 404 "http://$svc:8081/health/live/"
  direct "$svc:8081 /HEALTH/LIVE (maiusculas)" '0' 200 "http://$svc:8081/HEALTH/LIVE"
  direct "$svc:8081 /health/ready?x=1 (query)" '0' 200 "http://$svc:8081/health/ready?x=1"
done
DMOUNT=("${CA_M[@]}")
direct "consolidation-worker:8081 /health/ready" '0' 200 "http://consolidation-worker:8081/health/ready"
direct "consolidation-worker:8081 /api/v1 nao existe" '0' 404 "http://consolidation-worker:8081/api/v1"
direct "gateway:8081 /health/ready (porta de saude do gateway na rede app)" '0' 200 "http://gateway:8081/health/ready"
direct "gateway:8081 rota de negocio nao existe" '0' 404 "http://gateway:8081/api/v1/merchants/$A/consolidations/daily?date=$D"

sec "10. C-25: o que o container ve nas chaves/certificados (Docker Desktop/Windows; Linux nativo NAO validado)"
MSYS_NO_PATHCONV=1 docker exec cashflow-gateway-1 sh -c 'id; ls -ln /certs' 2>&1 | sed 's/^/  gateway: /'
MSYS_NO_PATHCONV=1 docker exec cashflow-transactions-api-1 sh -c 'id; ls -ln /certs' 2>&1 | sed 's/^/  transactions-api: /'
echo "  no host (Git Bash, modo emulado pelo NTFS):"; ls -l infra/pki/out/*.key | sed 's/^/  host: /' | head -4

echo; echo "RESULTADO: $FAIL verificacao(oes) com FALHA"
exit $FAIL
