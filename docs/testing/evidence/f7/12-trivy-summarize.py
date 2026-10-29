"""F7: resume os JSON do trivy gerados por 12-trivy-imagens.sh.
Uso: python -I 12-trivy-summarize.py <dir com list.txt, trivy-version.json e NN.json> <arquivo de saida .json>
Imprime versao do trivy, metadados da base (UpdatedAt/DownloadedAt), e por imagem: ID, RepoDigests, SO, contagem por severidade e por alvo.
Grava um JSON condensado (sem texto livre de CVE) com a lista de achados HIGH/CRITICAL e o total por severidade."""
import collections
import json
import os
import sys

src, dst = sys.argv[1:3]
ver = json.load(open(os.path.join(src, "trivy-version.json"), encoding="utf-8"))
db = ver.get("VulnerabilityDB", {})
print(f"trivy Version={ver.get('Version')} | VulnerabilityDB Version={db.get('Version')} UpdatedAt={db.get('UpdatedAt')} DownloadedAt={db.get('DownloadedAt')} NextUpdate={db.get('NextUpdate')}")
out = {"trivy": ver.get("Version"), "vulnerabilityDB": db, "images": []}
total_hc = 0
for line in open(os.path.join(src, "list.txt"), encoding="utf-8"):
    n, img = line.split()
    path = os.path.join(src, n + ".json")
    if not os.path.exists(path):
        print(f"{img}: SEM RESULTADO (trivy falhou) -> PENDENTE")
        out["images"].append({"image": img, "status": "PENDENTE"})
        continue
    r = json.load(open(path, encoding="utf-8"))
    md = r.get("Metadata", {})
    osinfo = md.get("OS", {})
    sev = collections.Counter()
    targets = []
    high = []
    for res in r.get("Results", []):
        vs = res.get("Vulnerabilities") or []
        c = collections.Counter(v.get("Severity") for v in vs)
        sev.update(c)
        targets.append({"target": res.get("Target"), "type": res.get("Type"), "total": len(vs), "bySeverity": dict(c)})
        for v in vs:
            if v.get("Severity") in ("HIGH", "CRITICAL"):
                high.append({"id": v.get("VulnerabilityID"), "severity": v.get("Severity"), "pkg": v.get("PkgName"),
                             "installed": v.get("InstalledVersion"), "fixed": v.get("FixedVersion"), "status": v.get("Status")})
    total_hc += len(high)
    print(f"{img}: ImageID={md.get('ImageID')} SO={osinfo.get('Family')} {osinfo.get('Name')} | total={sum(sev.values())} {dict(sev)} | HIGH+CRITICAL={len(high)}")
    for t in targets:
        print(f"    alvo {t['target']} ({t['type']}): {t['total']} {t['bySeverity']}")
    for h in high:
        print(f"    >>> {h['severity']} {h['id']} {h['pkg']} {h['installed']} corrigido-em={h['fixed']} status={h['status']}")
    out["images"].append({"image": img, "imageId": md.get("ImageID"), "repoDigests": md.get("RepoDigests"), "os": osinfo,
                          "total": sum(sev.values()), "bySeverity": dict(sev), "targets": targets, "highCritical": high})
print(f"TOTAL HIGH+CRITICAL em todas as imagens: {total_hc}")
json.dump(out, open(dst, "w", encoding="utf-8", newline="\n"), indent=1, ensure_ascii=False)
print(f"resumo gravado em {os.path.basename(dst)}")
