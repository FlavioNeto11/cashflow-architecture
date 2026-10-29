"""F7 Passo B item 6: reproduz a FALHA AMBIGUA de HTTP com Idempotency-Key.
Para cada tentativa: (1) envia o POST completo (cabecalhos + corpo) por socket TLS e FECHA a conexao sem ler a resposta, depois de
um atraso configuravel (0 a 200 ms) entre o fim do envio e o fechamento; (2) espera o servidor assentar; (3) consulta o PostgreSQL
(transactions e idempotency_keys para essa chave/descricao); (4) repete com a MESMA chave e o MESMO corpo e registra status e
cabecalho Idempotency-Replayed; (5) consulta de novo. Classificacao por tentativa:
  PERSISTIDA_ANTES_DO_RETRY : havia 1 linha antes do retry; o retry deve devolver 201 com Idempotency-Replayed: true
  NAO_PERSISTIDA_ANTES      : 0 linhas antes do retry; o retry cria (201 sem o cabecalho)
Em ambos os casos, o total final DEVE ser 1 linha em transactions e 1 em idempotency_keys. Qualquer outro valor e FALHA.
Se nenhuma tentativa cair na janela ambigua (PERSISTIDA_ANTES), o resultado e PARCIAL e e dito no resumo; o atraso NAO e ajustado ate "dar certo".
Uso: python -I 21-06-ambiguous-failure.py <merchant_id> <client_id> <effectiveDate> <runid> <tentativas>
Ambiente: F7_SECRET, F7_CA, F7_GW (host:porta), F7_KC (host:porta). O segredo nunca e impresso."""
import collections
import http.client
import json
import os
import socket
import ssl
import subprocess
import sys
import time
import urllib.parse

merchant, client, date, run, n = sys.argv[1:6]
n = int(n)
secret, ca, gw, kc = os.environ["F7_SECRET"], os.environ["F7_CA"], os.environ["F7_GW"], os.environ["F7_KC"]
ctx = ssl.create_default_context(cafile=ca)
host, port = gw.split(":")
port = int(port)
kh, kp = kc.split(":")
c = http.client.HTTPConnection(kh, int(kp), timeout=10)
c.request("POST", "/realms/cashflow/protocol/openid-connect/token",
          urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": client, "client_secret": secret,
                                  "scope": "transactions.write transactions.read consolidation.read"}),
          {"Content-Type": "application/x-www-form-urlencoded"})
tok = json.loads(c.getresponse().read())["access_token"]
c.close()


def psql(sql):
    r = subprocess.run(["docker", "exec", "-i", "cashflow-postgres-1", "psql", "-U", "migrator", "-d", "transactions_db",
                        "-v", "ON_ERROR_STOP=1", "-At", "-c", sql], capture_output=True, text=True, timeout=30)
    if r.returncode != 0:
        sys.exit("psql falhou: " + r.stderr[:200])
    return r.stdout.strip()


def counts(key, desc):
    tx = int(psql(f"select count(*) from transactions where merchant_id='{merchant}' and description='{desc}'"))
    ik = int(psql(f"select count(*) from idempotency_keys where merchant_id='{merchant}' and idempotency_key='{key}'"))
    ob = int(psql("select count(*) from outbox_messages o join transactions t on o.aggregate_id = t.id "
                  f"where t.merchant_id='{merchant}' and t.description='{desc}'"))
    return tx, ik, ob


def raw_abort(key, body, delay_ms):
    """Envia a requisicao inteira e fecha o socket sem ler a resposta (RST/FIN do cliente)."""
    req = (f"POST /api/v1/merchants/{merchant}/transactions HTTP/1.1\r\nHost: {host}:{port}\r\nAuthorization: Bearer {tok}\r\n"
           f"Idempotency-Key: {key}\r\nContent-Type: application/json\r\nContent-Length: {len(body)}\r\nConnection: close\r\n\r\n").encode() + body
    s = socket.create_connection((host, port), timeout=5)
    t = ctx.wrap_socket(s, server_hostname=host)
    t.sendall(req)
    if delay_ms:
        time.sleep(delay_ms / 1000)
    t.close()


def retry(key, body):
    cn = http.client.HTTPSConnection(host, port, context=ctx, timeout=10)
    cn.request("POST", f"/api/v1/merchants/{merchant}/transactions", body,
               {"Authorization": "Bearer " + tok, "Idempotency-Key": key, "Content-Type": "application/json"})
    r = cn.getresponse()
    r.read()
    out = (r.status, (r.getheader("Idempotency-Replayed") or "ausente").lower())
    cn.close()
    return out


delays = [0, 0, 2, 5, 10, 20, 50, 100, 200]
res = collections.Counter()
falhas = 0
print(f"# F7 21-06 falha ambigua - run={run} tentativas={n} data={date} atrasos(ms)={delays} (ciclo)")
for i in range(n):
    d = delays[i % len(delays)]
    key = f"f7-amb-{run}-{i:03d}"
    desc = f"f7-amb-{run}-{i:03d}"
    body = json.dumps({"type": "CREDIT", "amount": "1.00", "effectiveDate": date, "description": desc}).encode()
    try:
        raw_abort(key, body, d)
        abortou = "abort-ok"
    except Exception as e:  # noqa: BLE001
        abortou = f"abort-erro:{type(e).__name__}"
    time.sleep(1.5)
    antes = counts(key, desc)
    st, hdr = retry(key, body)
    depois = counts(key, desc)
    cls = "PERSISTIDA_ANTES_DO_RETRY" if antes[0] == 1 else ("NAO_PERSISTIDA_ANTES" if antes[0] == 0 else "ANOMALIA_ANTES")
    ok = depois == (1, 1, 1) and st in (200, 201) and ((cls == "PERSISTIDA_ANTES_DO_RETRY" and hdr == "true") or (cls == "NAO_PERSISTIDA_ANTES" and hdr in ("ausente", "false")))
    if not ok:
        falhas += 1
    res[(cls, st, hdr)] += 1
    print(f"  {i:03d} atraso={d:>3}ms {abortou} | antes(tx,idem,outbox)={antes} | retry=HTTP {st} replayed={hdr} | depois={depois} | {cls} | {'OK' if ok else 'FALHA'}")
print("resumo (classe, status do retry, Idempotency-Replayed) -> n:")
for k, v in sorted(res.items()):
    print(f"  {k} -> {v}")
persist = sum(v for k, v in res.items() if k[0] == "PERSISTIDA_ANTES_DO_RETRY")
print(f"tentativas={n} persistidas_antes_do_retry={persist} nao_persistidas_antes={n - persist} FALHAS={falhas}")
print("JANELA AMBIGUA " + ("ATINGIDA (ha tentativas em que o cliente nao viu a resposta e o lancamento ja existia)" if persist else "NAO ATINGIDA nesta execucao -> PARCIAL"))
sys.exit(1 if falhas else 0)
