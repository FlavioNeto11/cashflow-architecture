# ADR-0006 — Modelo de tenant, claim de merchant e política 401/403/404

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a DEC-02, RF-05 e RNF-04.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- O enunciado fala em **um** comerciante; multi-tenant é HIPÓTESE de engenharia para atender à orientação B (isolamento de tenant em segurança corporativa).
- RF-05: identidade autorizada para A não lê nem grava dados de B; `merchant_id` do payload não é fonte única de autorização. Oráculo: A = `64,80`, B = `10,00` na mesma data.
- Gap F0: definir claim de tenant, regra 403 × 404 e matriz papel × endpoint.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **A** Mono-tenant real (sem `merchant_id`) | Mínimo esforço | Não demonstra isolamento; RF-05 vira "projetado"; qualquer evolução exige migração de todas as tabelas |
| **B** Multi-tenant com `merchant_id` em **todas** as tabelas, no token (claim) e na rota; API exige rota = claim | Isolamento demonstrável com dois merchants (T-07); índices e PKs compostos por merchant; defesa clara | Mais testes; claim custom no IdP |
| **C** Multi-tenant com merchant **implícito** (só do token, sem `merchantId` na rota) | Impossível pedir outro merchant por URL | Impede papel de operação cross-tenant no futuro; menos REST; mesma necessidade de claim |
| **D** Banco/schema por tenant | Isolamento físico | Overkill para o desafio; operação por tenant |

## Decisão recomendada

Opção **B**, condicionada a DEC-02:

- **Claim** `merchant_id` (UUID, string) no access token, emitido pelo IdP por mapper de atributo do usuário (Authorization Code + PKCE) ou do cliente (Client Credentials). Um token representa **um** merchant no MVP. **Requisito do realm:** o atributo `merchant_id` é **editável apenas por administrador** (user profile do Keycloak com permissão de edição restrita a `admin`, sem edição pela conta do usuário nem por auto-registro); caso contrário o usuário troca o próprio tenant (R-37).
- **Autoria persistida**: `sub` e `azp`/`client_id` do token validado gravados em `transactions.created_by_sub`/`created_by_client_id` no lançamento e no estorno (não repúdio, `security.md` 7 F-A).
- **Regra de autorização em cada API**: `merchantId` da rota deve ser igual ao claim; recursos filhos (`/transactions/{id}`) são sempre consultados com `WHERE merchant_id = :claim AND id = :id`.
- **Política de respostas (uma só, referenciada por `security.md`):**
  - `401`: token ausente, inválido, expirado, assinatura/issuer/audience errados.
  - `403`: token válido sem o escopo/papel exigido pelo endpoint.
  - `404`: recurso sob **outro merchant** (rota ≠ claim ou id pertencente a outro tenant) — resposta idêntica à de recurso inexistente, sem mensagem distinguível, com evento de auditoria (sem PII) e métrica `authz_cross_tenant_denied_total`.
  - Alternativa aceitável: `403` também no caso cross-tenant (mais explícito, pequeno vazamento de existência); a escolha deve ser registrada aqui.
- **Papéis** (HIPÓTESE): `merchant_user` (humano), `merchant_system` (integração, Client Credentials). Papel `ops_operator` **não** acessa dados de merchants pela API no MVP; opera `recon-tool`, DLQ e dashboards. Acesso cross-tenant para operação é FUTURO.
- **Banco**: `merchant_id` em todas as PKs/índices relevantes; RLS do PostgreSQL com `SET app.merchant_id` por requisição é defesa em profundidade **ALVO**.
- **Eventos**: `merchantId` no payload e no header AMQP; worker rejeita evento sem merchant (DLQ).

## Consequências e trade-offs

- Dois merchants de demonstração no realm local (sem dados reais); T-07 exercita cross-tenant.
- `404` dificulta enumeração; o cliente legítimo com erro de configuração vê `404` em vez de `403`, mitigado pela auditoria.
- Mono-tenant permanece possível por configuração (um único merchant no realm) sem mudar o modelo.

## Validação e evidências previstas

- T-07: token de A em rota de B → `404`; token sem `consolidation.read` → `403`; token adulterado → `401`; A e B com lançamentos na mesma data → consolidados independentes (`64,80` / `10,00`).
- T-07 variante (proposta): usuário de A tenta alterar o próprio atributo `merchant_id` pela conta/Admin REST sem papel admin → negado; token seguinte mantém o claim original.
- Revisão de schema: nenhuma tabela sem `merchant_id`; `created_by_sub` preenchido em 100% das linhas de `transactions`.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
