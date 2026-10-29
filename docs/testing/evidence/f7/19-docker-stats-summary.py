"""F7: resume as amostras de `docker stats --no-stream` que o 05-k6-run.sh coletou durante cada rodada T-08
(linhas '[stats HH:MM:SS] servico cpu=X% mem=Y / Z;...' dentro de 06-t08-*.log). Nao mede nada novo.
Uso: python -I 19-docker-stats-summary.py   (executar nesta pasta). Saida: por rodada e por servico, n de amostras, CPU min-max e memoria maxima.
CPU% do docker stats e relativa a UM nucleo (100% = 1 nucleo; a VM do Docker Desktop tem 6). Amostras sao pontuais (~1 a cada 17 s),
nao uma serie continua: nao capturam picos curtos. Nao foram amostrados: k6 (o gerador de carga), consolidation-worker, rabbitmq."""
import glob
import re
import sys

rx_line = re.compile(r"^\[stats (\d\d:\d\d:\d\d)\] (.*)$")
rx_item = re.compile(r"([a-z-]+) cpu=([0-9.]+)% mem=([0-9.]+)(MiB|GiB)")
for path in sorted(glob.glob("06-t08-*.log")):
    if path.endswith("06-t08-all.log"):
        continue
    stats = {}
    first = last = None
    for line in open(path, encoding="utf-8"):
        m = rx_line.match(line.strip())
        if not m:
            continue
        first = first or m.group(1)
        last = m.group(1)
        for svc, cpu, mem, unit in rx_item.findall(m.group(2)):
            mib = float(mem) * (1024 if unit == "GiB" else 1)
            st = stats.setdefault(svc, {"cpu": [], "mem": []})
            st["cpu"].append(float(cpu))
            st["mem"].append(mib)
    print(f"{path}: amostras de {first} a {last}")
    for svc, st in stats.items():
        print(f"  {svc:18s} n={len(st['cpu']):2d} cpu%={min(st['cpu']):6.2f} a {max(st['cpu']):6.2f} | mem max={max(st['mem']):7.1f} MiB")
if not any(glob.glob("06-t08-*.log")):
    sys.exit("nenhum 06-t08-*.log nesta pasta")
