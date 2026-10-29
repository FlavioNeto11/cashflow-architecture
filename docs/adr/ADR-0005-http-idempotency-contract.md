# ADR-0005 — Contrato de idempotência HTTP para registro de lançamentos

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a RF-06 e RNF-08 (linhas "PENDENTE (ADR F1)" de `traceability.csv`).
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- Retry do cliente após timeout ou `201` perdido na rede não pode criar um segundo lançamento (RNF-08, T-03).
- RF-06: escopo da chave **por merchant**; mesma chave + mesmo payload → mesma resposta e um único lançamento; mesma chave + payload diferente → rejeição sem efeito.
- P-07: lançamento confirmado = `201` após commit durável.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **A** Cabeçalho `Idempotency-Key` obrigatório; registro `(merchant_id, key)` com hash do payload e resposta armazenada, gravado **na mesma transação** do lançamento | Alinha-se ao padrão de mercado e ao draft IETF `Idempotency-Key`; uma única barreira (a transação) impede duplicata e garante resposta estável; simples de testar | Cliente precisa gerar chave; tabela extra com retenção |
| **B** Cabeçalho opcional | Clientes simples funcionam sem chave | Enfraquece RNF-08 por padrão; comportamento bimodal |
| **C** Cliente fornece `transactionId` no corpo como PK natural | Sem tabela extra | Mistura identidade do recurso com idempotência; não detecta payload diferente para o mesmo id sem armazenar hash; resposta de replay precisa ser reconstruída |
| **D** Dedupe heurístico (mesmo merchant/valor/data em janela) | Sem cliente cooperativo | Rejeita lançamentos legítimos iguais; inaceitável para fluxo de caixa |

## Decisão recomendada

Opção **A**:

- `Idempotency-Key` **obrigatório** em `POST /transactions` e `POST /transactions/{id}/reversal`; ausência → `400`. Formato: string ≤ 255 (recomendado UUID v4).
- Hash: SHA-256 do corpo canônico (JSON com chaves ordenadas) + método + rota.
- Fluxo: dentro da transação do lançamento, `INSERT idempotency_keys(merchant_id, key, hash, transaction_id, status, body)`. Violação de PK → ler linha: hash igual → devolver `status`/`body` armazenados com `Idempotency-Replayed: true`; hash diferente → `422` (sem efeito).
- Concorrência: dois `POST` com a mesma chave em paralelo; o segundo bloqueia no índice único até o commit do primeiro e então segue o caminho de replay; se o primeiro ainda estiver em processamento e o bloqueio exceder timeout curto, `409` com `Retry-After`.
- Retenção: 7 dias (HIPÓTESE); limpeza por job; após expirar, a mesma chave cria novo lançamento (documentado).
- A chave é por merchant: a mesma string em merchants distintos não colide (PK composta).

## Consequências e trade-offs

- Dupla contabilização por retry HTTP é impossível **pela mesma barreira** que confirma o lançamento (uma transação, três inserts).
- A resposta armazenada inclui o corpo do lançamento (sem PII além do que o cliente enviou); não armazena cabeçalhos de autenticação.
- Códigos: `422` para payload divergente (draft IETF), `409` para concorrência; alternativa `409` para ambos é aceitável se o responsável preferir — registrar escolha.
- Idempotência de evento (Inbox) é tratada em ADR-0002; as duas camadas são independentes.

## Validação e evidências previstas

- T-03: 3 retries do crédito `100,00` → um lançamento, consolidado `100,00`; mesma chave com payload diferente → `422` e nenhum lançamento novo.
- T-06 (parte HTTP): dois `POST` concorrentes com a mesma chave → um lançamento.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
