"""F7 Passo B item 4: le os contadores de rate limiting do GATEWAY a partir do que o coletor OTLP temporario (21-04-otel-override.yml)
recebeu: rate_limited_total{route} (a metrica citada em P1-1) e aspnetcore.rate_limiting.requests{policy,result} (nativa do ASP.NET;
result = acquired | endpoint_limiter | global_limiter | request_canceled, o que separa 429 do teto GLOBAL de 429 da politica do ENDPOINT).
Contadores sao cumulativos desde o inicio do processo do gateway; o que vale e a DIFERENCA entre dois instantaneos. Serie ausente = 0.
Uso: python -I 21-04-counters.py snap  < saida de `docker logs cashflow-otelcol-1`   -> JSON em stdout
     python -I 21-04-counters.py diff antes.json depois.json                          -> diferencas nao nulas"""
import json
import re
import sys


def snap(text):
    chunks = text.split("ResourceMetrics #0")
    last = chunks[-1] if len(chunks) > 1 else ""
    out = {"_exportado_em": None}
    for blk in last.split("Metric #")[1:]:
        m = re.search(r"-> Name: (\S+)", blk)
        if not m or m.group(1) not in ("rate_limited_total", "aspnetcore.rate_limiting.requests"):
            continue
        name = m.group(1)
        for dp in blk.split("NumberDataPoints #")[1:]:
            attrs = dict(re.findall(r"-> ([\w.]+): Str\(([^)]*)\)", dp))
            val = re.search(r"Value: (\d+)", dp)
            ts = re.search(r"\nTimestamp: (.+)", dp)
            if val:
                key = name + "{" + ",".join(f"{k.replace('aspnetcore.rate_limiting.', '')}={v}" for k, v in sorted(attrs.items())) + "}"
                out[key] = int(val.group(1))
                if ts:
                    out["_exportado_em"] = ts.group(1).strip()
    return out


if sys.argv[1] == "snap":
    print(json.dumps(snap(sys.stdin.read()), sort_keys=True))
else:
    a, b = json.load(open(sys.argv[2])), json.load(open(sys.argv[3]))
    print(f"instantaneos: antes exportado em {a.get('_exportado_em')} | depois exportado em {b.get('_exportado_em')}")
    keys = sorted(set(a) | set(b) - {"_exportado_em"})
    diffs = [(k, a.get(k, 0), b.get(k, 0)) for k in keys if not k.startswith("_") and a.get(k, 0) != b.get(k, 0)]
    for k, x, y in diffs:
        print(f"  {k}: {x} -> {y} (delta {y - x})")
    if not diffs:
        print("  nenhuma serie mudou (delta 0 em rate_limited_total e aspnetcore.rate_limiting.requests)")
    rl = sum(y - x for k, x, y in diffs if k.startswith("rate_limited_total"))
    print(f"  rate_limited_total delta total = {rl}")
