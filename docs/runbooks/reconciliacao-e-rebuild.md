# Runbook: reconciliação (`reconcile`) e reconstrução (`rebuild`)

**Fonte de verdade:** `transactions` (`transactions_db`). `daily_consolidations` é derivada e reconstruível; a reconstrução parte de `transactions`, **nunca** do outbox.
Todo resultado é registrado em `reconciliation_runs` (trilha append-only: `recon_writer` só tem `SELECT, INSERT`).

## Quando usar
- Evento publicado e perdido (broker perdeu a fila/volume), `dailyNet` abaixo do oráculo, DLQ com `stale`/`out_of_retention`, suspeita de corrupção da projeção.
- Após restaurar `consolidation_db` ou o volume do broker.

## `reconcile` (compara e corrige o que divergir)
```bash
RECON="$C --profile tools run --rm recon-tool"
$RECON reconcile --merchant <uuid> --from 2026-10-01 --to 2026-10-31      # [EXECUTADO em F6: --from = --to = hoje, "clean divergencias=0"]
```
Saída por data: `clean` ou `corrected divergencias=N <tipo>=1`; tipos: `catch_up` (faltava evento), `corrected_corruption` (valor adulterado/errado), `anomaly` (**deve ser 0 sempre** no sentido de integridade, ADR-0002 item 7; escalar). Atenção: o **redrive da DLQ** também grava `anomaly` quando recusa uma mensagem ilegível, e isso é rotina de triagem, não corrupção: separe pelo `details.trail.mode = 'dlq_redrive'` (ver `dlq.md` seção 0).
Intervalo máximo por execução: 366 dias. Código de saída 0 = sem anomalia; 2 = requer atenção; 1 = falha; 64 = uso incorreto.
A guarda de retenção (29 d) impede `reconcile` de reaplicar evento cujo Inbox pode ter sido expurgado: para datas mais antigas use `rebuild`.

## `rebuild` (reconstrói a linha do dia a partir de `transactions` e do Inbox)
```bash
$RECON rebuild --merchant <uuid> --from 2026-10-03 --to 2026-10-03        # [EXECUTADO em F4 (round4); NÃO re-executado em F6]
```
Executa sob lock da linha; apaga e reinsere o Inbox do dia (`recon_writer` tem `DELETE` em `processed_messages` só para isso).

## Verificação
```bash
curl -s -H 'X-Dev-Sub: x' -H 'X-Dev-Client-Id: cashflow-web' -H "X-Dev-Merchant-Id: <uuid>" -H 'X-Dev-Scope: consolidation.read' \
  "http://127.0.0.1:18082/api/v1/merchants/<uuid>/consolidations/daily?date=2026-10-03"                      # perfil dev; [EXECUTADO em F6]
# papel recon_writer (SELECT na trilha); migrator só em emergência [trocado em passada documental; NÃO reexecutado, F4 usou migrator]
docker exec -i cashflow-postgres-1 psql -U recon_writer -d consolidation_db -At -c \
 "SELECT started_at, from_date, to_date, divergences, kinds FROM reconciliation_runs ORDER BY started_at DESC LIMIT 5"   # [EXECUTADO em F4]
```
Oráculo cruzado fonte × projeção: o bloco "7. cross-check" de `docs/testing/evidence/f4/verify-compose-scenarios.sh`.

## Cuidados
- Não rodar `rebuild` em massa durante pico (`recon_ro`/`recon_writer` têm `statement_timeout` de 60 s e limite de 3 conexões cada; disputa o servidor PostgreSQL compartilhado).
- O `recon-tool` usa `recon_ro` (fonte somente leitura; após F6 só `transactions`) e `recon_writer` (projeção, Inbox, trilha) e **não** exige o usuário de operação do broker (o redrive é AMQP com o usuário da aplicação).
- O aviso `Cannot load library libgssapi_krb5.so.2` na saída é inofensivo (Npgsql tenta Kerberos; a imagem não tem a biblioteca). Ajuste de imagem registrado como melhoria.
