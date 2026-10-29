"""F7 Passo B item 5: resume as amostras BRUTAS de `docker stats --no-stream` (21-05-stats-<label>.txt) coletadas por 05-k6-run.sh
(todos os containers em execucao, inclusive o k6, o worker e o rabbitmq; laco continuo, intervalo efetivo ~4 s).
Linha bruta: '[stats HH:MM:SS] servico cpu=X% mem=Y / Z;...'. CPU% do docker stats e relativa a UM nucleo (100% = 1 nucleo; a VM tem 6).
Amostras sao pontuais (nao ha serie continua) e incluem a fase de aquecimento e o encerramento; o proprio docker stats consome CPU.
Uso: python -I 21-05-stats-summary.py   (nesta pasta). Saida: por arquivo e por servico, n, CPU min/mediana/max, memoria maxima."""
import glob
import re
import statistics

rx_line = re.compile(r"^\[stats (\d\d:\d\d:\d\d)\] (.*)$")
rx_item = re.compile(r"([a-z0-9-]+) cpu=([0-9.]+)% mem=([0-9.]+)(MiB|GiB|KiB)")
for path in sorted(glob.glob("21-05-stats-*.txt")):
    stats, times = {}, []
    for line in open(path, encoding="utf-8"):
        m = rx_line.match(line.strip())
        if not m:
            continue
        times.append(m.group(1))
        for svc, cpu, mem, unit in rx_item.findall(m.group(2)):
            mib = float(mem) * {"GiB": 1024, "MiB": 1, "KiB": 1 / 1024}[unit]
            st = stats.setdefault(svc, {"cpu": [], "mem": []})
            st["cpu"].append(float(cpu))
            st["mem"].append(mib)
    if not times:
        continue
    print(f"{path}: {len(times)} amostras de {times[0]} a {times[-1]}")
    for svc, st in sorted(stats.items()):
        print(f"  {svc:24s} n={len(st['cpu']):3d} cpu% min={min(st['cpu']):6.2f} med={statistics.median(st['cpu']):6.2f} max={max(st['cpu']):6.2f} | mem max={max(st['mem']):7.1f} MiB")
