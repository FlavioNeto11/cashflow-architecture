# Requisitos funcionais — refinamento F0

Legenda: **EX** = explicitamente requerido; **PROP** = proposto para revisão. Não confundir com funcionalidade implementada. Os status desta tabela são os de F0/F1 (**PROPOSTO**). **Atualização F8 (2026-10-29):** os requisitos foram aprovados em G1; o estado de implementação e validação de cada um, com evidência, está em `traceability.csv` e `docs/architecture/implemented.md`.

| ID | Origem | Requisito | Aceite proposto (verificável) |
|---|---|---|---|
| RF-01 | EX | Registrar movimento de crédito | Requisição válida (valor > 0, escala ≤ 2, data de competência válida) retorna sucesso com identificador estável; consulta posterior do lançamento devolve exatamente o valor enviado (ex.: `100,00`). Valor `0`, negativo ou com 3 casas decimais é rejeitado com erro de validação (4xx) e nada é persistido (premissas P-01, P-04). |
| RF-02 | EX | Registrar movimento de débito | Mesmas regras de RF-01. Débito é armazenado com valor positivo e tipo débito (premissa P-04); débito de `35,20` aparece como `35,20` do tipo débito, não como `-35,20` de crédito. |
| RF-03 | EX | Consultar saldo consolidado diário | Consulta por merchant e data de competência retorna o valor definido em DEC-01. Oráculo: crédito `100,00` + débito `35,20` em `D` → `daily_net(D) = 64,80`; totais `credits = 100,00`, `debits = 35,20`. Data sem lançamentos → `0,00` (não erro; premissa P-08). A semântica exposta (`daily_net` ou `closing_balance`) depende de DEC-01. |
| RF-04 | PROP | Consultar lançamentos | Lista paginada por merchant e intervalo de competência, em ordem determinística; soma dos itens de uma data confere com RF-03 para a mesma data. |
| RF-05 | PROP | Isolar comerciante | Identidade autorizada para merchant A não lê nem grava dados do merchant B; o `merchant_id` do payload não é fonte única de autorização. Oráculo: A e B com lançamentos na mesma data têm consolidados independentes (A = `64,80`, B = `10,00`). |
| RF-06 | PROP | Registrar chave de idempotência | Escopo da `Idempotency-Key`: **por merchant** (unicidade `merchant_id` + chave; a mesma chave em merchants distintos não colide). Mesma chave + mesmo payload → mesma resposta e um único lançamento; mesma chave + payload diferente → rejeição (conflito) sem efeito. Oráculo: 3 retries do crédito `100,00` → consolidado continua `100,00`. |
| RF-07 | PROP | Aceitar lançamentos retroativos | Lançamento com competência anterior já consolidada corrige o agregado daquela data. Oráculo: `D` com `64,80` + crédito retroativo `5,00` em `D` → `daily_net(D) = 69,80`; datas posteriores mantêm `daily_net`; se `closing_balance` for exposto, todas as datas `≥ D` aumentam `5,00`. |
| RF-08 | PROP | Estornar sem editar histórico | Estorno cria lançamento inverso com referência ao original; original permanece consultável. Oráculo (sob P-09, opção recomendada): crédito `100,00` + débito `35,20` em `D` + estorno do débito → `daily_net(D) = 100,00`; com a alternativa de P-09, `daily_net(D) = 64,80` e `daily_net(data do estorno) = +35,20`. Segundo estorno do mesmo lançamento é rejeitado. |
| RF-09 | PROP | Expor frescor do consolidado | Resposta informa até que instante/evento a projeção está atualizada (`asOf`), se fizer parte do contrato aprovado. |
| RF-10 | PROP | Reconciliar agregados | Processo compara projeção com a soma dos lançamentos de origem por merchant/data, reporta divergências e corrige sem dupla contabilização (rodar duas vezes produz o mesmo resultado). |

## Premissas de domínio (todas PROP, sujeitas a aprovação)

| ID | Premissa | Motivo |
|---|---|---|
| P-01 | `amount` em tipo decimal exato (ex.: `numeric(18,2)` / `decimal` .NET), escala 2, arredondamento não aplicado silenciosamente: entrada com mais de 2 casas é rejeitada. | Enunciado não define precisão; `float`/`double` proibidos para dinheiro. |
| P-02 | Moeda única BRL (ISO 4217, 2 casas). Campo `currency` pode existir no contrato para evolução, mas valores diferentes de BRL são rejeitados no MVP. | Enunciado não cita moeda; evita conversão cambial. |
| P-03 | `occurred_at` persistido em UTC; `effective_date` (data de competência) é uma data sem hora. Quando o cliente não informa `effective_date`, ela é derivada de `occurred_at` no fuso de referência **America/Sao_Paulo**. `occurred_at` é atribuído pelo **servidor** no recebimento (recomendação); o cliente pode informar apenas `effective_date`. `effective_date` futura (posterior à data corrente em America/Sao_Paulo) é **rejeitada** (recomendação). | Consolidar por instante UTC desloca lançamentos noturnos para o dia seguinte; carimbo do servidor evita relógio do cliente como fonte de auditoria; rejeitar competência futura evita consolidado de datas que ainda não ocorreram. |
| P-04 | `entry_type` ∈ {CREDIT, DEBIT} com `amount > 0`, em vez de valor assinado. Escolha final registrada em ADR no F1. | Reduz ambiguidade de sinal e facilita validação. |
| P-05 | Saldo negativo é permitido (débitos maiores que créditos no dia). | Enunciado não proíbe; bloquear débito exigiria leitura consistente do saldo no caminho de escrita, acoplando-o ao consolidado. |
| P-06 | Exclusão e edição física não são oferecidas; correção via estorno (RF-08). | Auditabilidade; ver DEC-04. |
| P-07 | Lançamento confirmado = resposta de sucesso devolvida ao cliente após commit durável. | Base para RNF-07 (zero perda). |
| P-08 | Consulta de consolidado para data sem lançamentos retorna `0,00`, e não "não encontrado". Aplica-se a `daily_net` e aos totais; com `closing_balance`, a data sem movimento repete o fechamento anterior. | Contrato não definido pelo enunciado; evita confundir ausência de movimento com erro. |
| P-09 | Competência do estorno (RF-08): o estorno **herda a `effective_date` do lançamento original** (recomendação). Alternativa: competência = data do estorno; consequência numérica (quando a data do estorno difere de `D`) no oráculo de RF-08: `daily_net(D)` permanece `64,80` e o dia do estorno recebe `+35,20`. Vinculada a DEC-04 e DEC-05: na opção recomendada, estorno de lançamento de data passada é uma escrita retroativa, logo pressupõe que DEC-05 aceite retroativos. | Herdar a competência anula o efeito do lançamento errado na própria data; a alternativa preserva datas já consolidadas, mas deixa `daily_net(D)` com o valor incorreto. |

## Definições formais (ver DEC-01)

- `daily_net(m, D) = Σ créditos(m, effective_date = D) − Σ débitos(m, effective_date = D)`.
- `closing_balance(m, D) = opening_balance(m) + Σ daily_net(m, d)` para `d ≤ D`. Requer `opening_balance`, não fornecido pelo enunciado; premissa PROP: `0` na primeira competência do merchant.
- **Não afirmar qual corresponde ao termo do avaliador antes de decisão do responsável.**
