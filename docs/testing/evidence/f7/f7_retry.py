"""F7: reenvia, com a MESMA Idempotency-Key e o MESMO corpo, os POSTs do f7_poster.py que NAO terminaram em 201
(status 0 = erro de conexao/timeout do cliente, 5xx do gateway etc.). Simula o retry de um cliente apos falha ambigua.
Uso: python -I f7_retry.py <merchant_id> <client_id> <effectiveDate> <rotulo> <arquivo_log_do_poster>
Ambiente: F7_SECRET, F7_CA, F7_GW, F7_KC (como no f7_poster.py). Saida: distribuicao de status dos retries."""
import collections
import http.client
import json
import os
import ssl
import sys
import time
import urllib.parse

merchant, client, date, label, logpath = sys.argv[1:6]
secret, ca, gw, kc = os.environ["F7_SECRET"], os.environ["F7_CA"], os.environ["F7_GW"], os.environ["F7_KC"]
ctx = ssl.create_default_context(cafile=ca)
h, p = kc.split(":")
c = http.client.HTTPConnection(h, int(p), timeout=10)
c.request("POST", "/realms/cashflow/protocol/openid-connect/token",
          urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": client, "client_secret": secret,
                                  "scope": "transactions.write transactions.read consolidation.read"}),
          {"Content-Type": "application/x-www-form-urlencoded"})
tok = json.loads(c.getresponse().read())["access_token"]
c.close()
host, port = gw.split(":")
body = json.dumps({"type": "CREDIT", "amount": "1.00", "effectiveDate": date, "description": label})
stats = collections.Counter()
replayed_stats = collections.Counter()  # (status final, Idempotency-Replayed): replayed=true => a chave JA estava gravada antes do retry
keys = [l.split(",")[1] for l in open(logpath, encoding="utf-8") if l.strip() and l.split(",")[2] != "201"]
for key in keys:
    st = 0
    replayed = "n/a"
    for _ in range(5):  # ate 5 tentativas por chave (a stack pode ainda estar subindo)
        try:
            cn = http.client.HTTPSConnection(host, int(port), context=ctx, timeout=5)
            cn.request("POST", f"/api/v1/merchants/{merchant}/transactions", body,
                       {"Authorization": "Bearer " + tok, "Idempotency-Key": key, "Content-Type": "application/json"})
            r = cn.getresponse()
            r.read()
            st = r.status
            replayed = (r.getheader("Idempotency-Replayed") or "ausente").lower()
            cn.close()
        except Exception:  # noqa: BLE001
            st = 0
        if st in (200, 201):
            break
        time.sleep(1)
    stats[st] += 1
    replayed_stats[f"{st}/replayed={replayed}"] += 1
print(f"rotulo={label} chaves_nao_201_no_poster={len(keys)} status_dos_retries={dict(stats)}")
print(f"rotulo={label} retries_por_status_e_cabecalho_Idempotency-Replayed={dict(replayed_stats)}  (replayed=true: a chave ja estava gravada ANTES do retry; replayed=ausente com 201: o retry CRIOU o lancamento)")
