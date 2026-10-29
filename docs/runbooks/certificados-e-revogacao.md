# Runbook: certificados (mTLS e borda) e revogação por blocklist

**Escopo.** PKI **local de demonstração** (`infra/pki/make-certs.sh`, CA própria). Em produção o alvo é PKI/cofre corporativo com CRL/OCSP (`security.md`); nada disto é essa PKI. Validade: CA 825 dias, folhas 180 dias (informação de F5).

## 1. Pré-requisito: arquivos antes do `up`

Gateway e APIs montam **arquivos individuais** de `infra/pki/out/` (P1-1; `ca.key`, `foreign-ca.key` e `neg-*` nunca entram em container). Se o arquivo não existir, o Docker cria um **diretório** no lugar e o serviço falha na partida (fail-closed). Sempre `bash infra/pki/make-certs.sh` antes do primeiro `up`.

## 2. Rotação de certificados (demo)

```bash
bash infra/pki/make-certs.sh --force
docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env up -d --force-recreate gateway transactions-api consolidation-api
```
Os certificados são lidos **na partida**; não há reload. O gateway responde 502 enquanto as APIs reiniciam. **[NÃO EXECUTADO]** nesta fase (o `--force-recreate` e a rotação não têm log); só a existência dos arquivos montados foi verificada (`docs/testing/evidence/f6/29-certs-file-mounts.log`). Se a CA for recriada, **todos** os serviços precisam reiniciar juntos (CA nova invalida os certificados antigos). O script **não** sobrescreve `blocked-thumbprints.txt` (só o cria se faltar): como certificados novos têm outro thumbprint, entradas antigas ficam obsoletas e podem ser removidas (edite em lugar).

## 3. Revogação por blocklist de thumbprint (efeito: handshake novo)

1. Thumbprint SHA-1 em hexadecimal, sem `:` (ex.: `openssl x509 -in infra/pki/out/gateway-client.crt -noout -fingerprint -sha1 | tr -d ':' | sed 's/.*=//'`).
2. Acrescente **em lugar** a `infra/pki/out/blocked-thumbprints.txt` (`printf '%s\n' "$TP" >> ...`; não troque o arquivo por outro).
3. As APIs releem o arquivo sem reinício (por mtime); o **handshake novo** é recusado. **[EXECUTADO]** `docs/testing/evidence/f6/29b-gateway-scenarios-rerun.log`, seção 7: chamada direta com o certificado bloqueado falha (curl exit 56); pelo gateway, a conexão já aberta no pool segue 200.
4. Efeito imediato: `docker compose ... restart gateway`; a seguir o gateway recebe 502 (**[EXECUTADO]**, mesma seção). Sem restart, a janela é o `PooledConnectionLifetime` do gateway (2 min).
5. Reverter: esvazie o arquivo (mantendo o cabeçalho de comentário) e confirme 200 pelo gateway (**[EXECUTADO]**, mesma seção).

**Limite:** só blocklist de thumbprints; não há CRL/OCSP (ALVO).

## 4. Cliente Windows (curl com Schannel)

Com a CA local, o `curl` do Git for Windows (Schannel) exige checagem de revogação que a CA não publica: use `--ssl-revoke-best-effort` junto de `--cacert infra/pki/out/ca.crt`. Não é defeito do servidor; em Linux/OpenSSL a flag não existe.
