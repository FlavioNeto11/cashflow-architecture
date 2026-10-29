# ADR-0007 — Estorno e lançamentos retroativos

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a DEC-04, DEC-05, P-06, P-09, RF-07 e RF-08.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- O enunciado não trata correção de lançamentos nem competência passada.
- P-06 (HIPÓTESE): sem edição/exclusão física; correção por estorno (auditabilidade).
- RF-07: lançamento com competência anterior corrige o agregado daquela data.
- RF-08: estorno cria lançamento inverso referenciando o original; segundo estorno rejeitado.
- P-09: competência do estorno herda a do original (recomendação) ou é a data do estorno (alternativa).
- Com `daily_net` (ADR-0004), um retroativo afeta uma única linha da projeção.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **Correção — A** Estorno como lançamento inverso com `reversal_of` único | Histórico imutável; trilha; reaproveita o mesmo evento e a mesma projeção | Endpoint extra; regra de unicidade |
| **Correção — B** Edição/exclusão sob regras | Simples para o usuário | Reescreve histórico; invalida consolidados já lidos; exige eventos de compensação complexos |
| **Correção — C** Fora do escopo (lançamento inverso manual sem vínculo) | Nada a construir | Auditoria fraca; dupla reversão não detectável |
| **Competência do estorno — P-09 recomendada** herda `effective_date` do original | Anula o efeito na própria data (oráculo `100,00`) | É escrita retroativa; pressupõe DEC-05 aceitando retroativos |
| **Competência do estorno — alternativa** data do estorno | Datas passadas nunca mudam | `daily_net(D)` permanece errado (`64,80`) e o dia do estorno recebe `+35,20` |
| **Retroativos — aceitar** (DEC-05) | Corrige lançamento esquecido; custo baixo com `daily_net` | Consolidado de data passada muda após leitura; `closing_balance` posterior muda (absorvido pelo cálculo na leitura) |
| **Retroativos — rejeitar** competência < hoje | Imutabilidade do passado | Impede correções; força estorno com data atual; conflita com P-09 recomendada |
| **Retroativos — limitar** (janela, ex.: 90 dias) | Compromisso operacional | Número arbitrário sem requisito; FUTURO se o negócio pedir |

## Decisão recomendada

- **Correção A**: `POST /merchants/{m}/transactions/{id}/reversal` cria lançamento com tipo inverso, mesmo valor, `reversal_of = id`, competência **herdada** (P-09 recomendada). Índice único parcial em `reversal_of` impede segundo estorno (`409`). Estorno de estorno não é permitido (`422`). O original permanece consultável com indicação de que foi estornado (derivada na leitura).
- **Retroativos aceitos** sem janela no MVP; competência **futura** rejeitada (P-03).
- Evento: o estorno gera `TransactionRegistered v1` comum (tipo inverso, `reversalOf` preenchido); a projeção soma normalmente — sem lógica especial no worker.
- Prioridade: A priorização inicial (F0) classificou RF-07 retroativo = **Should** e RF-08 estorno = **Could**. **Proposta F1, aprovada em G1-11 e G1-12:** retroativo = Must (é apenas não rejeitar competência passada); estorno = Should, com fallback "projetado" se o prazo apertar. Estorno e retroativo registram `created_by_sub`/`created_by_client_id` (ADR-0006).

## Consequências e trade-offs

- Oráculos: crédito `100,00` + débito `35,20` em D + estorno do débito → `daily_net(D) = 100,00` (P-09 recomendada); alternativa: `64,80` em D e `+35,20` no dia do estorno. Retroativo de `5,00` em D → `69,80`.
- Consolidados já lidos podem mudar; `asOf` e `entriesCount` evidenciam a mudança; clientes devem tratar o consolidado como eventual.
- Sem janela de retroatividade, um erro operacional pode alterar datas muito antigas; mitigação: auditoria e, se o negócio pedir, janela como FUTURO.

## Validação e evidências previstas

- T-01 (caso de estorno a acrescentar ao playbook): oráculo `100,00`; segundo estorno → `409`.
- T-09: retroativo altera só a data afetada; `closingBalance` posterior reflete.
- T-03: estorno com `Idempotency-Key` repetida → um estorno.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
