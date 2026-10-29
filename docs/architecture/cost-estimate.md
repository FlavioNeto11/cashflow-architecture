# Estimativa de custos — HIPÓTESES (nada cotado, nada medido em nuvem)

**Status (revisão F6, 2026-10-29): tudo neste documento é HIPÓTESE.** Não houve acesso a páginas de preço nem a calculadoras nesta sessão: **nenhum preço, faixa ou total tem fonte consultada**, e nenhum recurso pago foi provisionado. O que o documento entrega: premissas de volume (inventadas, rotuladas), dimensionamento em unidades, fórmula de composição e as fontes a consultar. A única grandeza **medida** é o consumo de memória/CPU do Compose local (seção 4, S0), de uma amostra única. Decisão relacionada: G1-06 APROVADA em G1 / ADR-0010 ACEITO (alvo agnóstico; AWS é apenas exemplo para custo, sem escolha de provedor).

Exemplo de provedor usado **apenas** para mapear serviços gerenciados: AWS (HIPÓTESE; não é escolha). Região hipotética: `sa-east-1` (São Paulo), coerente com o fuso de competência.

---

## 1. Premissas de volume (todas HIPÓTESES; o enunciado não informa volumes)

| Parâmetro | S1 — Piloto | S2 — Crescimento | Justificativa |
|---|---|---|---|
| Merchants ativos | 10 | 1.000 | S1 reflete a demonstração; S2 é extrapolação |
| Lançamentos/dia | 10.000 | 1.000.000 | ~0,12 RPS e ~12 RPS médios de `POST` |
| Pico de leitura do consolidado | 50 RPS (EX) | 500 RPS | S2 multiplica por 10 |
| Leituras/dia | 500.000 | 5.000.000 | Picos de 50 RPS por períodos curtos + base |
| Tamanho por lançamento (linha + índices + outbox) | ~1,5 KB | ~1,5 KB | Estimativa de engenharia |
| Crescimento de `transactions_db`/mês | ~0,45 GB | ~45 GB | lançamentos/dia × 30 × 1,5 KB |
| Retenção de outbox publicada / inbox | 7 / 30 dias | 7 / 30 dias | ADR-0002 |
| Logs | ~1 KB por requisição; ~0,5 GB/dia | ~7 GB/dia | sem `amount`/PII |
| Traces amostrados | 10% | 1% | HIPÓTESE |
| Disponibilidade-alvo | 99,9% (PROP, RNF-11) | 99,9% | Não validável no desafio |

---

## 2. Dimensionamento por cenário (unidades, não preços)

| Componente | S0 — Local (Compose) | S1 — Piloto | S2 — Crescimento | Serviço gerenciado de exemplo (AWS) |
|---|---|---|---|---|
| gateway | 1 container | 2 tarefas × 0,5 vCPU / 1 GB | 3 tarefas × 1 vCPU / 2 GB | ECS Fargate |
| transactions-api | 1 | 2 × 0,5 vCPU / 1 GB | 4 × 1 vCPU / 2 GB | ECS Fargate |
| consolidation-api | 1 | 2 × 0,5 vCPU / 1 GB | 4 × 1 vCPU / 2 GB | ECS Fargate |
| consolidation-worker | 1 | 1 × 0,5 vCPU / 1 GB | 2 × 1 vCPU / 2 GB | ECS Fargate |
| PostgreSQL | 1 container, volume local | 1 instância pequena (2 vCPU / 8 GB) **Multi-AZ**, 50 GB gp3, backup 7 dias | 1 instância média (4 vCPU / 16 GB) Multi-AZ + réplica de leitura, 500 GB, PITR | RDS for PostgreSQL |
| Broker | 1 container | 1 nó pequeno (single-instance; **sem HA**, declarado) ou cluster de 3 nós pequenos para HA | Cluster 3 nós médios | Amazon MQ for RabbitMQ |
| LB + WAF | — | 1 ALB + WAF (regras gerenciadas básicas) | Idem, maior tráfego | ALB, AWS WAF |
| IdP | Keycloak container | Cognito (até limite gratuito de MAU) **ou** Keycloak em 2 tarefas Fargate | IdP corporativo existente | Cognito / autogerido |
| Observabilidade | 1 container | ~15 GB logs/mês + métricas custom + traces | ~210 GB logs/mês | CloudWatch Logs/Metrics/X-Ray |
| Segredos/PKI | arquivos locais | ~10 segredos; certificados ACM privados ou PKI própria | idem | Secrets Manager, ACM |
| Transferência de dados | — | < 50 GB/mês | ~500 GB/mês | Data transfer |

---

## 3. Fórmula de composição e fontes a consultar

```
custo_mensal(cenário) =
    Σ tarefas × (vCPU × preço_vCPU_hora + GB × preço_GB_hora) × 730
  + instância_DB × preço_hora × 730 × (2 se Multi-AZ) + GB_storage × preço_GB + backup_extra
  + nós_broker × preço_hora × 730 + GB_storage_broker × preço_GB
  + ALB (horas + LCU) + WAF (web ACL + regras + requisições)
  + IdP (MAU acima do nível gratuito ou tarefas Fargate)
  + logs_GB_ingeridos × preço_ingestão + GB_armazenados × preço_retenção + métricas_custom + traces
  + segredos × preço_mensal + transferência_GB × preço_GB
  + custo humano de operação (horas/mês × taxa) — a estimar à parte
```

Páginas oficiais a consultar (registrar data da consulta e região): preços de ECS Fargate, RDS for PostgreSQL, Amazon MQ, Elastic Load Balancing, AWS WAF, Cognito, CloudWatch, Secrets Manager e transferência de dados, todas em `aws.amazon.com/<serviço>/pricing`. Usar a calculadora oficial do provedor e anexar a exportação.

---

## 4. Faixa por cenário

| Cenário | Faixa | Rótulo |
|---|---|---|
| S0 — Local | Sem custo de nuvem; custo = máquina do responsável. **MEDIDO (amostra única, `docs/testing/evidence/f6/19-docker-stats-idle.log`, 2026-10-29T08:28:03Z, data registrada no cabeçalho do log; stack dev + coletor, sem carga):** memória em uso por container: keycloak ~671 MiB, rabbitmq ~120 MiB, transactions-api ~89 MiB, consolidation-api ~77 MiB, consolidation-worker ~42 MiB, otel-collector ~39 MiB, postgres ~35 MiB (soma ~1,05 GiB = 1074 MiB). CPU instantânea 0,0-0,7% exceto transactions-api 25% (polling do outbox ou probe coincidente; não investigado) | Medido, mas **não generaliza**: uma amostra, sem carga, sem as demais fases do ciclo; não serve para dimensionar S1/S2 |
| S1 — Piloto | Ordem de grandeza: **centenas de USD/mês**, dominada por RDS Multi-AZ e pelo broker (cluster de 3 nós pode igualar ou superar o banco) | **HIPÓTESE SEM FONTE CONSULTADA NESTA SESSÃO — não é cotação; **PENDENTE**: confirmar com calculadora oficial, registrando fonte e data (não feito em F6)** |
| S2 — Crescimento | Ordem de grandeza: **baixos milhares de USD/mês**, dominada por banco (instância + réplica + storage), broker em cluster e ingestão de logs | **HIPÓTESE SEM FONTE CONSULTADA NESTA SESSÃO — não é cotação** |

Valores unitários e totais por linha: **NÃO PREENCHIDOS** (F6 não teve acesso a fontes de preço em 2026-10-29). Para preencher: consultar as páginas oficiais da seção 3, registrar URL, região, moeda e data da consulta ao lado de cada preço, aplicar a fórmula e anexar a exportação da calculadora. Preços mudam; um valor sem data e fonte não deve ser citado.

---

## 5. Opções que mudam a ordem de grandeza

| Alavanca | Efeito | Trade-off |
|---|---|---|
| Broker single-instance em S1 | Reduz muito o custo do broker | Sem HA do broker; perdas publicadas-não-consumidas dependem da reconciliação (ADR-0002) |
| Sem broker (worker lendo a outbox) | Elimina o broker | Acoplamento ao schema da fonte; ver sensibilidade do ADR-0001 |
| Um servidor de banco (opção C) vs dois (B) | B dobra o custo de banco | B isola o store da projeção |
| Logs sem `amount`/PII e amostragem de traces | Reduz ingestão | Menos detalhe forense (compensado por ids) |
| Cognito no nível gratuito vs Keycloak autogerido | Cognito evita tarefas e operação | Claims custom e PKCE suportados; dependência de fornecedor |
| Reserva/savings plans | Reduz computação e banco em 20–40% típicos (HIPÓTESE geral de mercado, não cotação) | Compromisso de 1–3 anos |

---

## 6. Custo humano operacional (a estimar à parte)

Categorias a dimensionar (**PENDENTE**, não feito em F6), em horas/mês: operação de banco (backup/restore, upgrades), broker (DLQ, upgrades), IdP (realm, usuários), certificados/segredos, observabilidade (alertas, dashboards), resposta a incidentes, releases. Gerenciado reduz as duas primeiras; autogerido reduz fatura e aumenta horas.

---

## 7. O que este documento **não** afirma

- Nenhum valor monetário medido ou cotado.
- Nenhum provedor escolhido (G1-06/ADR-0010: alvo agnóstico; AWS só como exemplo de mapeamento).
- Nenhuma disponibilidade garantida (RNF-11 não validável).
