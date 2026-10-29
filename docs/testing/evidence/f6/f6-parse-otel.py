# Gerador do log 10: python -I docs/testing/evidence/f6/f6-parse-otel.py docs/testing/evidence/f6/09-otel-collector-raw.log > 10-otel-traces-summary.log
# (copiado de um diretorio temporario em passada documental; o log 10 foi gerado em 2026-10-29 ~08:10Z a partir do log 09)
import re,sys,collections
svc=None; kind=None; tr=None; cur={}
spans=[]
for line in open(sys.argv[1],encoding="utf-8",errors="replace"):
    if "ResourceSpans #" in line: kind="spans"
    elif "ResourceMetrics #" in line: kind="m"
    elif "ResourceLog" in line: kind="l"
    m=re.search(r"service\.name: Str\(([^)]+)\)",line)
    if m: svc=m.group(1)
    if kind=="spans":
        m=re.match(r"\s+Trace ID\s+: (\w+)",line)
        if m: cur={"trace":m.group(1),"svc":svc}
        m=re.match(r"\s+Name\s+: (.+)",line)
        if m and cur: cur["name"]=m.group(1).strip(); spans.append(cur); cur={}
by=collections.defaultdict(set)
for s in spans: by[s["trace"]].add((s["svc"],s["name"]))
multi={t:v for t,v in by.items() if len({x[0] for x in v})>1}
print("spans:",len(spans),"traces:",len(by),"traces com >1 servico:",len(multi))
for t,v in list(multi.items())[:3]:
    print(t); [print("   ",x) for x in sorted(v)]
print("nomes de spans por servico:")
c=collections.Counter((s["svc"],s["name"]) for s in spans)
for k,n in sorted(c.items()): print("  ",n,k)
