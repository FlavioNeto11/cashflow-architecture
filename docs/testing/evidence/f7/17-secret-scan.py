"""F7 17: varredura de segredos no REPOSITORIO INTEIRO (arquivos rastreados + nao rastreados que nao estejam no .gitignore, sem filtro de
extensao; binarios com NUL sao ignorados). Procura (a) cada valor de infra/compose/.env com >= 6 caracteres e (b) padrao de JWT (eyJ...).
NUNCA imprime valores: so nome de variavel, contagem e caminho do arquivo. Variaveis *_USER e KEYCLOAK_ADMIN (nome de usuario) sao
identificadores e ficam fora. Valores iguais aos do .env.example sao sinalizados como 'igual ao .env.example' (exemplo versionado de proposito)
sem imprimir o valor. Uso: python -I 17-secret-scan.py <raiz do repo>"""
import os
import re
import subprocess
import sys

root = sys.argv[1]
os.chdir(root)


def parse(path):
    out = {}
    if not os.path.exists(path):
        return out
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\r\n")
        if not line or line.lstrip().startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        out[k.strip()] = v.strip()
    return out


env = parse("infra/compose/.env")
example = parse("infra/compose/.env.example")
USERS = lambda k: k.endswith("_USER") or k == "KEYCLOAK_ADMIN"
secrets, skipped = {}, 0
for k, v in env.items():
    if len(v) < 6:
        continue
    if USERS(k):
        skipped += 1
        continue
    secrets[k] = v

files = subprocess.run(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], capture_output=True, check=True).stdout.split(b"\0")
files = [f.decode("utf-8", "replace") for f in files if f]
rx_jwt = re.compile(rb"eyJ[A-Za-z0-9_-]{20,}")
hits = {k: [] for k in secrets}
jwt_files = []
scanned = binary = 0
enc = {k: v.encode() for k, v in secrets.items()}
for f in files:
    if not os.path.isfile(f):
        continue
    try:
        data = open(f, "rb").read()
    except OSError:
        continue
    if b"\0" in data:
        binary += 1
        continue
    scanned += 1
    for k, v in enc.items():
        if v in data:
            hits[k].append(f)
    if rx_jwt.search(data):
        jwt_files.append(f)

print(f"arquivos candidatos (git ls-files --cached --others --exclude-standard)={len(files)} varridos(texto)={scanned} binarios_ignorados={binary}")
print(f"variaveis do .env checadas (valor >= 6 chars; excluidas *_USER e KEYCLOAK_ADMIN)={len(secrets)} | excluidas por serem usuario={skipped}")
real = 0
for k, fl in hits.items():
    if not fl:
        continue
    same = example.get(k) == secrets[k]
    only_example = all(f.replace("\\", "/").endswith("infra/compose/.env.example") for f in fl)
    kind = "igual ao .env.example" if same else "DIFERENTE do .env.example"
    if not (same and only_example):
        real += 1
    print(f"OCORRENCIA var={k} [{kind}] arquivos={len(fl)}: " + ", ".join(fl[:5]))
print(f"variaveis com ocorrencia={sum(1 for fl in hits.values() if fl)} | fora do esperado (valor diferente do exemplo, ou valor-exemplo fora do .env.example)={real}")
print(f"arquivos com padrao JWT={len(jwt_files)}" + (": " + ", ".join(jwt_files[:5]) if jwt_files else ""))
sys.exit(0 if real == 0 and not jwt_files else 1)
