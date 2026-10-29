# Runbook: mensagem na DLQ (`consolidation.dlq`)

**Princípio:** DLQ > 0 é sempre alarme (**PROPOSTA**). Nunca descartar sem registro. Cada republicação passa pelo `recon-tool dlq redrive`, que grava a trilha
(`reconciliation_runs`, `kind=dlq_redrive`) **antes** de publicar e republica como mensagem nova (mesmo `message-id` e payload; sem `x-death`/`x-delivery-count`).
O consumo é idempotente (Inbox): reentrega de evento já aplicado não soma duas vezes.

## 0. Dois significados de `anomaly` (não confundir)
`anomaly` aparece em `reconciliation_runs` com **dois sentidos**:
1. **Integridade (ADR-0002 item 7):** o `reconcile` achou no Inbox algo que a fonte não tem (`E = I \ S` não vazio). Deve ser **0 sempre**; é incidente (`reconciliacao-e-rebuild.md`).
2. **Triagem rotineira do redrive (`DlqRedriver.cs:80,83`):** o redrive **recusa** como `anomaly` a mensagem cujo `occurredAt` está ilegível (ex.: `malformed_json`) ou sem routing key original. Isto é esperado e não indica corrupção financeira.
Como separar: as linhas do sentido 2 trazem `details` com `trail.mode = 'dlq_redrive'`. **Consulta PROPOSTA, NÃO EXECUTADA** (colunas `kinds`/`details` jsonb conforme a migração; sintaxe jsonb não testada aqui):
```sql
SELECT started_at, kinds, details->'trail'->>'messageId' AS message_id
FROM reconciliation_runs WHERE details->'trail'->>'mode' = 'dlq_redrive' AND kinds ? 'anomaly' ORDER BY started_at DESC;
```
Alertar integridade só nas linhas **sem** `trail.mode='dlq_redrive'`. Um `kind` distinto (ex.: `dlq_unparseable`) fica como melhoria.

## 1. Inspecionar sem consumir
Usa o usuário de **operação** (o da aplicação recebe 401 na API de gestão). Use o `mq()` de `README.md` (credenciais em subshell, fora do argv).
```bash
docker exec cashflow-rabbitmq-1 rabbitmqctl -q list_queues name messages | grep dlq                  # [EXECUTADO] contagem confiável
mq -X POST "$MQ/queues/%2F/consolidation.dlq/get" -d '{"count":10,"ackmode":"ack_requeue_true","encoding":"auto","truncate":200}'   # [EXECUTADO] (ack_requeue_true = só espiar)
```
Cabeçalhos úteis: `x-reason`, **`x-reason-detail`** (detalhe; o nome correto está em `Topology.cs:25`), `x-original-routing-key`, `x-death[].reason`.
O script `docs/testing/evidence/f6/f6-dlq-scenarios.sh` filtra isso e foi **reexecutado** na passada 3 (`27-dlq-scenarios-rerun.log`, corrigido para `x-reason-detail`). Resultado real: a mensagem não-JSON chegou à DLQ com `x-reason=malformed_json` e **sem** `x-reason-detail` (o worker só grava o detalhe quando conhece o campo, `ConsolidationConsumer.cs:338-340`; `malformed_json` não tem campo); a mensagem que estourou `x-delivery-limit` chegou com `x-death[].reason=delivery_limit` e sem `x-reason`. Um `x-reason-detail` preenchido (p.ex. `invalid_payload`) **não foi observado** nesta rodada. O `x-field=None` do log `11-dlq-scenarios.log` era artefato do script antigo.

## 2. Triagem por motivo
| `x-reason` | Causa | Ação |
|---|---|---|
| `malformed_json` **[observado em F6]** | Corpo não é JSON | Corrigir o produtor. O redrive **NÃO republica**: recusa como `anomaly` ("occurredAt ilegível", `DlqRedriver.cs:80`; **[EXECUTADO]** `03-rabbit-app-user-verify.log` linha 20: "recusada anomaly"). A mensagem fica na DLQ até decisão explícita (passo 5). |
| `invalid_payload` | Campo inválido/ausente (ver `x-reason-detail`) | Bug de contrato do produtor. **Se** `occurredAt` for legível, o redrive republica e a mensagem **volta à DLQ**; se ilegível, é recusada como `anomaly`. [derivado do código; NÃO EXECUTADO] |
| `merchant_mismatch` | `merchantId` do corpo difere do cabeçalho/rota | Idem; investigar como possível incidente de isolamento de tenant (segurança). |
| `constraint_violation` | Violação de constraint (ex.: CHECK) | Investigar o dado no banco; `reconcile` da data após corrigir. |
| `projection_overflow` | Estouro de `numeric` da projeção (não transitório) | **Redrive recusa** (`projection_overflow`). Decisão sobre teto/rebuild; escalar. |
| `stale` | Evento mais antigo que a guarda do Inbox (29 d) | **Redrive recusa** (`out_of_retention`). `rebuild` da linha (passo 4). |
| **(ausente)** com `x-death[].reason=delivery_limit` **[observado em F6]** | O **broker** esgotou `x-delivery-limit=5`. Causas possíveis: banco fora por horas, consumidor derrubando o canal, **ou falha não classificada repetida** (mensagem possivelmente inválida) | **Não presumir que a mensagem é válida.** Inspecionar o corpo (passo 1) e a causa nos logs do worker antes de qualquer redrive. O caminho delivery_limit → redrive **NÃO foi demonstrado**: a mensagem de teste tinha corpo de 2 bytes, e o redrive a recusaria como `anomaly`. |

Prova do último caso (só a **ausência de `x-reason`** e o `x-death`): `docs/testing/evidence/f6/11-dlq-scenarios.log` (mensagem `f6-deliverylimit-1`: `x-reason=None`, `x-death: reason=delivery_limit queue=consolidation.transaction-registered`).
Nota sobre contagens: o log 11 mostra 5 mensagens na DLQ e o log 03 mostra 3 depois de **1** publicação porque execuções anteriores de F6 deixaram mensagens de teste na fila (a DLQ não foi esvaziada entre os roteiros).

## 3. Redrive (causa corrigida e payload legível)
```bash
RECON="$C --profile tools run --rm recon-tool"
$RECON dlq redrive --id <message-id>      # uma mensagem                  [--id NÃO EXECUTADO]
$RECON dlq redrive --all                   # todas, cada uma decidida individualmente   [EXECUTADO: só recusas, republicadas=0 (log 03)]
```
Saída: `republicadas=N recusadas=M ignoradas=K devolvidas_nesta_execucao=J`. **Não foi demonstrada nenhuma republicação bem-sucedida em F6** (`republicadas=0` no log 03).
**Ressalva `devolvidas_nesta_execucao`:** conta cópias com o **mesmo `message-id`** já vistas na execução (`DlqRedriver.cs:50-54`); só a **1ª cópia** de cada `message-id` é tratada por execução e as demais ficam sem ACK. Com duplicatas na DLQ (como `f6-invalid-1` ×3 no log 03: `recusadas=1 devolvidas=2`), rode o redrive de novo até a fila esvaziar ou inspecione as cópias.
Kinds gravados em `reconciliation_runs` pelo redrive: `dlq_redrive` (antes de publicar), `out_of_retention`, `projection_overflow`, `anomaly` (recusas), **`redrive_failed`** (publicação não confirmada; a mensagem permanece na DLQ, `DlqRedriver.cs:104`).
O `reconcile`/`rebuild` pode gravar ainda `lock_timeout` e `source_timeout` (linha ativa/fonte lenta; repetir; `Reconciler.cs:43`).
Mensagem republicada que o worker rejeita de novo **volta à DLQ**. Verificar: `list_queues` (DLQ diminui), `dailyNet` convergiu e a trilha:
```bash
# papel recon_writer (SELECT, INSERT na trilha); o migrator é dono/DDL e só em emergência.  [papel trocado em passada documental; NÃO reexecutado. Em F4 foi executado com migrator]
docker exec -i cashflow-postgres-1 psql -U recon_writer -d consolidation_db -At -c \
 "SELECT kinds, count(*) FROM reconciliation_runs GROUP BY 1 ORDER BY 2 DESC"
```

## 4. Evento com mais de 29 dias (retenção do Inbox: 30 d, expurgo em 31 d - HIPÓTESES)
O redrive **recusa** (`out_of_retention`): o Inbox pode já ter expurgado o registro e reentregar somaria em dobro. Correção: `reconcile`/`rebuild` da data afetada
(`reconciliacao-e-rebuild.md`); `rebuild` reconstrói a linha a partir de `transactions`, a fonte de verdade. Depois remover a mensagem (passo 5), com a trilha da recusa como registro.
**PROPOSTA de alerta:** idade da mensagem mais antiga na DLQ > 7 d (crítico, antes dos 29 d). A métrica `dlq_oldest_age_seconds` **não existe** hoje no código (exigiria coleta na API do broker).
`dlq_messages_total` conta só as cópias feitas **pelo worker** para a DLX; **não** conta as que chegam por `delivery_limit` (broker).
Limite aberto: **nenhum job de expurgo** de outbox (7 d) / Inbox (30 d) existe (C-15).

## 5. Remover mensagens da DLQ (somente após registro e decisão)
Usar **só** o `recon-tool`, que grava uma trilha por mensagem em `reconciliation_runs` (`details.mode = dlq_purge`: `messageId`, `x-reason`, routing key, data efetiva, SHA-256 do corpo e o motivo informado) **antes** do ACK que a remove:
```bash
docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env --profile tools run --rm recon-tool dlq purge --id <message-id> --reason "<motivo e responsável>"
docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env --profile tools run --rm recon-tool dlq purge --all --reason "<motivo e responsável>"
```
`--reason` é obrigatório (até 500 caracteres, sem quebra de linha). Com `--id` inexistente o comando sai com código de atenção e não remove nada. O purge não republica nem corrige o consolidado: a correção da data afetada é `reconcile`/`rebuild` (`reconciliacao-e-rebuild.md`).
Se o processo cair entre a gravação da trilha e o ACK, a mensagem volta para a DLQ e a trilha fica registrada; ao repetir o purge, sai uma segunda trilha para o mesmo `messageId`.
**Não usar** `rabbitmqctl purge_queue` nem `DELETE /api/queues/%2F/consolidation.dlq/contents` da API de gestão: esvaziam a fila sem nenhum registro (PF-09).
Evidência: testes `Purge_RecordsTrailPerMessage_ThenAcks_WithoutRepublishing` e `PurgeById_RemovesOnlyThatMessage_AndUnknownIdIsReported` (`docs/testing/evidence/f9/05-pf09-purge.log`) e cenário D5 na stack (`docs/testing/evidence/f9/18-rerun-08-dlq.log`).
