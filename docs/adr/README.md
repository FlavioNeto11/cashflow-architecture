# Architecture Decision Records

Os ADRs 0001–0011 foram **ACEITOS em G1** (2026-10-29, aprovação do responsável em `docs/project/approvals.md`); o conteúdo foi proposto em F1. Aceite não é validação: o que foi implementado e medido está em `docs/architecture/implemented.md` e `docs/requirements/traceability.csv`.

Convenção de nomes: `ADR-000N-slug.md`, numerados em sequência.

## Índice

| ADR | Título | Responde a | Status |
|---|---|---|---|
| [ADR-0001](ADR-0001-runtime-topology.md) | Topologia de runtime: lançamentos e consolidação (opções A/B/C; recomendação C) | DEC-03, RNF-01, RNF-02 | ACEITO em G1 (2026-10-29) |
| [ADR-0002](ADR-0002-delivery-integrity.md) | Integridade de publicação e consumo: Outbox, broker, Inbox, DLQ e replay | RNF-07, RNF-08, RF-10, 5 falhas obrigatórias | ACEITO em G1 (2026-10-29) |
| [ADR-0003](ADR-0003-identity-boundaries.md) | Identidade, perímetro e limites de confiança | RNF-04, orientação B | ACEITO em G1 (2026-10-29) |
| [ADR-0004](ADR-0004-consolidation-semantics.md) | Semântica do consolidado diário e `closing_balance` na leitura | DEC-01, P-08, RF-03/07/09 | ACEITO em G1 (2026-10-29) |
| [ADR-0005](ADR-0005-http-idempotency-contract.md) | Contrato de idempotência HTTP (`Idempotency-Key` por merchant) | RF-06, RNF-08 | ACEITO em G1 (2026-10-29) |
| [ADR-0006](ADR-0006-tenant-model-and-authorization.md) | Modelo de tenant, claim `merchant_id` e política 401/403/404 | DEC-02, RF-05, RNF-04 | ACEITO em G1 (2026-10-29) |
| [ADR-0007](ADR-0007-reversal-and-backdated-entries.md) | Estorno e lançamentos retroativos | DEC-04, DEC-05, P-06, P-09, RF-07/08 | ACEITO em G1 (2026-10-29) |
| [ADR-0008](ADR-0008-dotnet-version.md) | Versão do .NET ((a) .NET 10 LTS com instalação autorizada / (b) .NET 9 com EOL 2026-11-10) | DEC-10 | ACEITO em G1 (2026-10-29) |
| [ADR-0009](ADR-0009-request-loss-interpretation.md) | Interpretação de "5% de perda" e método do ensaio de 50 RPS | DEC-09, RNF-02/03/09/10 | ACEITO em G1 (2026-10-29) |
| [ADR-0010](ADR-0010-deployment-context.md) | Contexto de deploy, topologia-alvo e exemplo para custo | DEC-08, RNF-06/11/12, A-16/A-17 | ACEITO em G1 (2026-10-29) |
| [ADR-0011](ADR-0011-idp-gateway-and-mtls-scope.md) | IdP local, API Gateway e escopo de mTLS no protótipo | DEC-06, DEC-07, RNF-04 | ACEITO em G1 (2026-10-29) |

Matriz de decisão pontuada para ADR-0001: `docs/architecture/decision-matrix.csv`. Registro de riscos: `docs/project/risk-register.csv`.
