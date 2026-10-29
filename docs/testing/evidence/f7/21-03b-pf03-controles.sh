#!/usr/bin/env bash
# F7 Passo B item 3 (complemento): PF-03 para TODOS os caracteres de controle Cc citados na revisao (\t \n \r, DEL, C1) e
# controles positivos (texto normal com acento, 200 caracteres, emoji) que DEVEM continuar 201. Pelo gateway, pela stack reconstruida.
# Uso: bash 21-03b-pf03-controles.sh > 21-03b-pf03-controles.log 2>&1 ; echo "exit=$?" >> ...
. "$(dirname "${BASH_SOURCE[0]}")/f7-common.sh"
FAIL=0; RUN=$(date +%s); D=2026-07-02
echo "# F7 21-03b - PF-03 todos os Cc + controles positivos - $(date -u +%FT%TZ)"
mktokens
n0=$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and effective_date='$D'"); o0=$($PSQL_T -c "select count(*) from outbox_messages")
# O corpo vai por ARQUIVO (--data-binary @arquivo): o curl.exe do Windows converte argumentos para a pagina de codigo ANSI, o que
# transformaria "ç" em um byte invalido (E7) e geraria 500 por UTF-8 invalido (ver secao "UTF-8 invalido" e PF-17).
chk() { # chk <rotulo> <esperado> <json description (ja escapado)>
  printf '%s' '{"type":"CREDIT","amount":"1.00","effectiveDate":"'$D'","description":"'"$3"'"}' > "$S/body.json"
  local c; c=$(curl -sS $REVOKE --cacert "$CA" -D "$H" -o "$B" -w '%{http_code}' -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TAW"     -H "Idempotency-Key: f7-21-03b-$RUN-$RANDOM" -H 'Content-Type: application/json; charset=utf-8' --data-binary @"$S/body.json")
  if [ "$c" = "$2" ]; then echo "  OK    $1 -> HTTP $c"; else echo "  FALHA $1 -> HTTP $c (esperado $2)"; FAIL=$((FAIL+1)); fi
}
sec "Rejeitados (esperado 422): caracteres de controle"
chk 'TAB \t'            422 'a\tb'
chk 'LF \n'             422 'a\nb'
chk 'CR \r'             422 'a\rb'
chk 'CRLF'              422 'a\r\nb'
chk 'NUL \u0000'        422 '\u0000'
chk 'BEL \u0007'        422 '\u0007'
chk 'US \u001f'         422 '\u001f'
chk 'DEL \u007f'        422 '\u007f'
chk 'C1 \u0085 (NEL)'   422 'a\u0085b'
chk 'C1 \u009f'         422 'a\u009fb'
sec "Aceitos (esperado 201): controles positivos"
chk 'texto simples'            201 'f7 21-03b ok'
chk 'acentos'                  201 'Reposição de estoque - ação'
chk 'emoji (par substituto)'   201 'caixa 😀'
chk 'espaco e pontuacao'       201 'a b, c; d: (e) - f/g'
sec "UTF-8 invalido no corpo (ACHADO PF-17: esperado 400 ou 422, observado 500 -> NAO e falha do PF-03; contado a parte)"
printf '{"type":"CREDIT","amount":"1.00","effectiveDate":"%s","description":"a\xe7b"}' "$D" > "$S/bad.json"
cbad=$(curl -sS $REVOKE --cacert "$CA" -D "$H" -o "$B" -w '%{http_code}' -X POST "$GW/merchants/$A/transactions" -H "Authorization: Bearer $TAW"   -H "Idempotency-Key: f7-21-03b-bad-$RUN" -H 'Content-Type: application/json' --data-binary @"$S/bad.json")
echo "  byte E7 isolado (Latin-1) em description -> HTTP $cbad ($([ "$cbad" = 500 ] && echo 'ACHADO: 500' || echo 'sem 500'))"
n1=$($PSQL_T -c "select count(*) from transactions where merchant_id='$A' and effective_date='$D'"); o1=$($PSQL_T -c "select count(*) from outbox_messages")
echo; echo "linhas criadas: $((n1-n0)) (esperado 4 = so os controles positivos) | outbox: $((o1-o0)) (esperado 4)"
[ "$((n1-n0))" = 4 ] && [ "$((o1-o0))" = 4 ] || { echo "FALHA: contagem de linhas/outbox"; FAIL=$((FAIL+1)); }
echo "FALHAS=$FAIL"; exit $FAIL
