"""F7: disparo SIMULTANEO (barreira) de POSTs pelo gateway real. Usa so a biblioteca padrao.

Uso: python -I f7_race.py <modo> <merchant> <data> <n> [chave|original_id] [rotulo]
Ambiente: F7_TOKEN (JWT; nunca impresso), F7_CA (caminho do ca.crt), F7_GW (host:porta, ex. 127.0.0.1:8443).
Modos:
  same_key      n POSTs com a MESMA Idempotency-Key e o MESMO corpo
  same_key_diff n POSTs com a MESMA Idempotency-Key e valores DIFERENTES (amount = 1.00 + i)
  distinct      n POSTs com chaves distintas, amount 1.00 cada
  reversal_same n estornos do lancamento <original_id> com a MESMA chave
  reversal_diff n estornos do lancamento <original_id> com chaves DIFERENTES
Saida: distribuicao de status, de Idempotency-Replayed e de ids devolvidos (so codigos e contagens).
"""
import collections
import http.client
import json
import os
import ssl
import sys
import threading
import time
import uuid

mode, merchant, date, n = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
extra = sys.argv[5] if len(sys.argv) > 5 else ""
label = sys.argv[6] if len(sys.argv) > 6 else "f7"
token, ca, gw = os.environ["F7_TOKEN"], os.environ["F7_CA"], os.environ["F7_GW"]
host, port = gw.split(":")
ctx = ssl.create_default_context(cafile=ca)
barrier = threading.Barrier(n)
results = [None] * n
key0 = extra if mode == "same_key" and extra else f"{label}-{uuid.uuid4().hex}"


def call(i):
    if mode in ("same_key", "same_key_diff", "distinct"):
        path = f"/api/v1/merchants/{merchant}/transactions"
        amount = "10.00" if mode == "same_key" else f"{1 + i:d}.00" if mode == "same_key_diff" else "1.00"
        body = json.dumps({"type": "CREDIT", "amount": amount, "effectiveDate": date, "description": label})
        key = key0 if mode in ("same_key", "same_key_diff") else f"{label}-{uuid.uuid4().hex}"
    else:
        path = f"/api/v1/merchants/{merchant}/transactions/{extra}/reversal"
        body = json.dumps({"reason": label})
        key = key0 if mode == "reversal_same" else f"{label}-{uuid.uuid4().hex}"
    conn = http.client.HTTPSConnection(host, int(port), context=ctx, timeout=30)
    conn.connect()  # handshake antes da barreira: mede a corrida no servidor, nao o TLS
    barrier.wait()
    t0 = time.perf_counter()
    try:
        conn.request("POST", path, body, {"Authorization": "Bearer " + token, "Idempotency-Key": key, "Content-Type": "application/json"})
        r = conn.getresponse()
        data = r.read()
        try:
            rid = json.loads(data).get("id")
        except Exception:
            rid = None
        results[i] = (r.status, r.getheader("Idempotency-Replayed"), rid, time.perf_counter() - t0)
    except Exception as e:  # noqa: BLE001 - registrar a falha como resultado
        results[i] = ("EXC:" + type(e).__name__, None, None, time.perf_counter() - t0)
    finally:
        conn.close()


threads = [threading.Thread(target=call, args=(i,)) for i in range(n)]
t_start = time.perf_counter()
for t in threads:
    t.start()
for t in threads:
    t.join()
print(f"modo={mode} n={n} rotulo={label} duracao_total={time.perf_counter() - t_start:.2f}s")
print("status:", dict(collections.Counter(str(r[0]) for r in results)))
print("Idempotency-Replayed:", dict(collections.Counter(str(r[1]) for r in results)))
ids = collections.Counter(r[2] for r in results if r[2])
print(f"ids distintos nas respostas com corpo: {len(ids)} ->", sorted(ids.values(), reverse=True)[:5])
lat = sorted(r[3] for r in results)
print(f"latencia (s): min={lat[0]:.3f} p50={lat[len(lat) // 2]:.3f} max={lat[-1]:.3f}")
