"""F7: gerador de POSTs a taxa constante pelo gateway REAL, com registro por requisicao (para provar "zero perda de
lancamento confirmado" cruzando o log com o PostgreSQL). Biblioteca padrao apenas.

Uso: python -I f7_poster.py <merchant_id> <client_id> <effectiveDate> <req_por_s> <segundos> <rotulo> <arquivo_log>
Ambiente: F7_SECRET (segredo do cliente; nunca impresso), F7_CA, F7_GW (host:porta do gateway), F7_KC (host:porta do Keycloak).
Cada linha do log: epoch_ms,chave,status,latencia_ms  (status 0 = erro de conexao/timeout do cliente de 3 s).
Cada POST tem Idempotency-Key unica e description=<rotulo>; valor 1.00 CREDIT. Saida: distribuicao de status e latencias.
"""
import collections
import http.client
import json
import os
import ssl
import sys
import time
import urllib.parse
import uuid

merchant, client, date, rate, seconds, label, logpath = sys.argv[1:8]
rate, seconds = float(rate), float(seconds)
secret, ca, gw, kc = os.environ["F7_SECRET"], os.environ["F7_CA"], os.environ["F7_GW"], os.environ["F7_KC"]
ctx = ssl.create_default_context(cafile=ca)
tok, tok_exp = None, 0.0


def token():
    global tok, tok_exp
    if tok and time.time() < tok_exp - 30:
        return tok
    h, p = kc.split(":")
    c = http.client.HTTPConnection(h, int(p), timeout=10)
    body = urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": client, "client_secret": secret,
                                   "scope": "transactions.write transactions.read consolidation.read"})
    c.request("POST", "/realms/cashflow/protocol/openid-connect/token", body, {"Content-Type": "application/x-www-form-urlencoded"})
    j = json.loads(c.getresponse().read())
    tok, tok_exp = j["access_token"], time.time() + j["expires_in"]
    c.close()
    return tok


host, port = gw.split(":")
stats, lats = collections.Counter(), []
start = time.perf_counter()
n = 0
with open(logpath, "w", encoding="utf-8") as log:
    while True:
        due = start + n / rate
        if due - start >= seconds:
            break
        wait = due - time.perf_counter()
        if wait > 0:
            time.sleep(wait)
        key = f"{label}-{uuid.uuid4().hex}"
        body = json.dumps({"type": "CREDIT", "amount": "1.00", "effectiveDate": date, "description": label})
        t0 = time.perf_counter()
        status = 0
        try:
            c = http.client.HTTPSConnection(host, int(port), context=ctx, timeout=3)
            c.request("POST", f"/api/v1/merchants/{merchant}/transactions", body,
                      {"Authorization": "Bearer " + token(), "Idempotency-Key": key, "Content-Type": "application/json"})
            r = c.getresponse()
            r.read()
            status = r.status
            c.close()
        except Exception:  # noqa: BLE001 - erro de conexao/timeout = status 0
            status = 0
        ms = (time.perf_counter() - t0) * 1000
        stats[status] += 1
        lats.append(ms)
        log.write(f"{int(time.time() * 1000)},{key},{status},{ms:.1f}\n")
        n += 1
lats.sort()
q = lambda p: lats[min(len(lats) - 1, int(p * len(lats)))]
print(f"rotulo={label} enviados={n} status={dict(stats)} lat_ms p50={q(0.5):.1f} p95={q(0.95):.1f} p99={q(0.99):.1f} max={lats[-1]:.1f}")
