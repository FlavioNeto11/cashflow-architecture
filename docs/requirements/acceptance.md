# Critérios de aceite e gates

> **Mapeamento (proposto em F0 e usado desde G1).** O projeto tem apenas dois gates de aprovação do responsável: **G1** (após F1, antes de F2) e **G2** (após F8, antes da entrega final). Os itens abaixo foram preservados; os títulos legados recebem a correspondência a seguir. `docs/project/approvals.md` registra só G1 e G2.
>
> | Seção legada | Correspondência no workflow |
> |---|---|
> | G0 | Pré-F0; não é gate no workflow |
> | G1 | **G1** (gate humano) |
> | G2 | Checkpoint interno das fases F3–F5 (não é gate humano) |
> | G3 | Checkpoint interno das fases F6–F7 (não é gate humano) |
> | G4 | **G2** (gate humano final) |
>
> **Atualização F8 (2026-10-29).** As caixas marcadas em G1 citam o registro de aprovação do responsável. As marcadas nas seções G2–G4 são fatos técnicos com a evidência ao lado, marcados em F8; elas **não** são o aceite final, que é o gate humano **G2**, aceito pelo responsável em 2026-10-29. Caminhos `evidence/...` são relativos a `docs/testing/`. "Local" quer dizer um host com Docker Desktop; nenhum resultado vale para produção.

## G0 — Contexto do desafio disponível
- [x] Contexto e instruções presentes. *(não prova qualquer implementação)*
- [ ] Revisão do responsável realizada. *(não registrada como item separado; cabe ao responsável)*

## G1 — Arquitetura aprovada pelo responsável
Aprovado pelo responsável em 2026-10-29T02:31:29Z (`docs/project/approvals.md`).
- [x] Decisões abertas avaliadas. *(DEC-01..DEC-10 resolvidas por G1-01..G1-08, `decisions-needed.md`)*
- [x] Alternativas arquiteturais confrontadas com requisitos. *(matriz A 125 / B 130 / C 140; G1-03)*
- [x] C4 L1/L2/L3 e topologia alvo coerentes. *(G1, `approvals.md`)*
- [x] Threat model, OIDC/JWT, Gateway, mTLS e tenant boundaries mapeados. *(G1-02, G1-05, `security.md`)*
- [x] ADRs de trade-offs assinados pelo responsável. *(ADR-0001..0011 aceitos em G1, `docs/adr/README.md`)*
- [x] Prioridades implementação x projetado explicitadas. *(G1-12)*
- [x] Premissas P-01..P-09 de `RF.md` aprovadas ou rejeitadas individualmente. *(P-01..P-08 em G1-09; P-09 em G1-04)*
- [x] Fórmula de perda e timeout do cliente (DEC-09) aprovados. *(G1-07)*

## G2 — Core demonstrável
- [x] Lançamento crédito/débito testado. *(`evidence/f7/03-idempotency-concurrency.log`; `evidence/f9/16-rerun-03-idempotency.log`, `03` reexecutado com as imagens atuais, 0 FALHA; suíte com 364 testes em `evidence/f9/06-suite-transactions-api.log` a `09-suite-persistence.log`; `evidence/f4/verify-compose-scenarios-round4.log:42-52`)*
- [x] Consolidação diária correta. *(`evidence/f7/21-05-base-get50-120s.log`, corpo de cada GET comparado ao oráculo SQL; `evidence/f4/verify-compose-scenarios-round4.log:54-69`, 64,80, retroativo 5,00 e `closingBalance` 69,80)*
- [x] Queda de consolidação não afeta transações. *(`evidence/f9/17-rerun-07-resilience.log` R1, imagens atuais: worker e consolidation-api parados, 200 POSTs, todos 201)*
- [x] Outbox/reprocessamento/idempotência testados (ou alternativa aprovada). *(com as imagens atuais: `evidence/f9/17-rerun-07-resilience.log` R2 e SIGKILL R3, R4a e R4b; `evidence/f9/16-rerun-03-idempotency.log`; `evidence/f9/18-rerun-08-dlq.log`, D1 a D5 com purge com trilha; e `evidence/f7/21-06-ambiguous-failure.log`. Os logs de F7 anteriores ao rebuild ficam como histórico)*
- [x] Acesso não autorizado e cruzamento de tenant negados. *(`evidence/f9/15b-rerun-02-security.log`, 87 OK e 0 FALHA, com as imagens atuais; `evidence/f9/13-token-sem-merchant.log`, token sem `merchant_id` recusado com 403; suíte `CashFlow.Security.Tests`)*

## G3 — Resiliência/qualidade
- [x] k6 50 RPS e taxa de erro validada ou gap aberto. *(validada localmente: `evidence/f7/21-05-base-get50-120s.log`, 1 tenant, depois do rebuild, 6001 req, 0,00%, p95 6,88 ms; `evidence/f7/06-t08-get-two-tokens.log`, 2 tokens, antes do rebuild, 6001 req, 0,00%; `evidence/f7/06-t08-get-single-10min.log`, 30001 req; perfil de 10 tenants do ADR-0009 em `evidence/f9/14-t08-10tenants.log`, 6001 req a 50,01/s, 0,00%, p95 7,09 ms. Ressalvas: 1 data, carga sem rajada, k6 no mesmo host)*
- [x] Testes de casos e falhas com evidências. *(`docs/testing/evidence-log.md`, F6 e F7; lacunas em `docs/testing/test-plan.md` e `docs/project/final-audit.md`)*
- [ ] Logs, métricas e trace demonstrados. *(**Parcial:** OTLP exportado só com o override de observabilidade, `evidence/f7/21-04-otel-override.yml`; faltam métricas de lag, idade da DLQ e idade da outbox)*
- [x] CI compilação/testes efetivamente verde. *(`.github/workflows/ci.yml`; `evidence/f9/21-ci-github-actions.log`, run 40925558828: build Release sem avisos e 364 testes aprovados em `ubuntu-latest`. A stack Compose e o k6 não rodam no CI)*

## G4 — Entrega
- [x] README roda localmente de ponta a ponta. *(executado em F8 a partir de volumes vazios (`down -v`), com `.env` e PKI preexistentes: `evidence/f8/01-readme-quickstart.log`, 3ª execução, com o README final: POST 201, consolidado 200 com `credits` 100.00 e oráculo SQL 1 linha. Histórico: a 1ª falhou com 401 porque o README não esperava o `keycloak-init` (`evidence/f8/01a-readme-quickstart-tentativa1.log`), corrigido com `--wait` e `docker wait`; a 2ª passou antes das correções da revisão de arquitetura (`evidence/f8/01b-readme-quickstart-tentativa2.log`). Só Windows/Git Bash com Docker Desktop)*
- [x] Documentação no GitHub público, mediante autorização. *(publicado em 2026-10-29 com autorização do responsável, G1-17)*
- [x] Arquitetura alvo vs implementada vs futura separadas. *(`docs/architecture/target.md` × `docs/architecture/implemented.md` seção 4, com rótulos VALIDADO/PARCIAL/PROJETADO/FUTURO)*
- [ ] Apresentação/defesa técnica ensaiada com perguntas difíceis. *(o ensaio cabe ao responsável)*
- [x] Lista final de gaps e riscos honesta. *(`docs/project/final-audit.md` seção 4, 20 itens; `docs/project/risk-register.csv`; a avaliação de honestidade é do responsável em G2)*

## Aceite final
- [x] **G2 — aceite humano da entrega** *(aceito pelo responsável em 2026-10-29; `docs/project/approvals.md`)*.
