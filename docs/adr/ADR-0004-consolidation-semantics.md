# ADR-0004 — Semântica do consolidado diário e cálculo de `closing_balance`

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a DEC-01, P-08 e RF-03/RF-07/RF-09.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- RF-03 (EX): consultar "saldo diário consolidado". O enunciado não define se é o fluxo líquido do dia (`daily_net`) ou a posição acumulada (`closing_balance`). Definições formais em `RF.md`.
- `closing_balance` exige `opening_balance`, que o enunciado não fornece; um retroativo altera todas as datas posteriores (cascata).
- P-08: data sem movimento devolve `0,00`, não erro.
- RNF-02/09: a leitura precisa sustentar 50 RPS com p95 preliminar ≤ 300 ms.
- A escolha semântica é do responsável; este ADR define **como** cada opção seria servida.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **A** Expor só `daily_net` (+ `credits`, `debits`, `entriesCount`, `asOf`) | Alinha-se a "fluxo de caixa" e "consolidado diário"; retroativo afeta uma linha; sem premissa de abertura | Não responde a quem lê "saldo" como posição acumulada |
| **B** Expor `closing_balance` **materializado** por dia | Leitura O(1) | Cada retroativo recalcula todas as datas ≥ D (cascata) dentro do worker; concorrência e reconciliação mais complexas; exige `opening_balance` |
| **C** Expor `daily_net` por padrão e `closing_balance` **calculado na leitura** (`Σ daily_net(d ≤ D)` sobre `daily_consolidations`), ativado por `include=closingBalance` | Sem cascata na escrita; a projeção tem **uma linha por merchant/dia**, logo a soma percorre no máximo alguns milhares de linhas por merchant com índice na PK `(merchant_id, effective_date)`; premissa de abertura explícita na resposta | Custo de leitura cresce linearmente com o histórico do merchant (mitigável com snapshot mensal como FUTURO); depende da premissa `opening_balance = 0` |

## Decisão recomendada

Opção **C**, condicionada a DEC-01:

- Contrato padrão: `credits`, `debits`, `dailyNet`, `entriesCount`, `asOf` (RF-09).
- Extensão `include=closingBalance`: `closingBalance = Σ dailyNet(d ≤ date)` calculado na consulta, com campo `openingBalanceAssumption: "ZERO_AT_FIRST_DATE"` (HIPÓTESE) até existir `merchant_opening_balances` (FUTURO).
- Data sem linha: zeros e `asOf: null` (P-08). Com `closingBalance`, repete o fechamento anterior por construção da soma.
- Se o responsável escolher "apenas acumulado" como semântica principal, a mesma implementação serve; apenas o campo padrão muda. Se escolher "ambos sempre", retirar o `include`.

## Consequências e trade-offs

- A projeção continua aditiva e comutativa; retroativos e estornos continuam baratos (ADR-0007).
- `closing_balance` sob 50 RPS: a soma por PK é uma varredura de índice por merchant; a validar em T-08 com massa declarada. Se p95 estourar, snapshot mensal materializado (FUTURO) sem alterar o contrato.
- `asOf` é "último evento aplicado à linha", não "consistente até T" (sem ordenação garantida); documentado no contrato.
- Oráculos (de `RF.md`): `daily_net(1) = 64,80`, `daily_net(2) = 10,00`, `closing_balance(2) = 74,80`; com retroativo de 5,00 no dia 1: `daily_net(1) = 69,80`, `closing_balance(2) = 79,80`.

## Validação e evidências previstas

- T-02: consolidado exato por merchant/data.
- T-09: retroativo altera só a data afetada e `closingBalance` das datas posteriores.
- T-08: p95/p99 com e sem `include=closingBalance`, reportados separadamente.
- T-11: `asOf` e lag visíveis.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
