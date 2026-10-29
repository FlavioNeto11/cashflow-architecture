# ADR-0010 — Contexto de deploy, topologia-alvo e exemplo para custo

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a DEC-08, RNF-06, RNF-11, RNF-12 e A-16/A-17.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- Orientação B: topologia de infraestrutura e estratégia de deployment; diferenciais de custo (A-17) e arquitetura de transição se necessária (A-16).
- Nenhum provedor, limite de infraestrutura ou disponibilidade foi informado; nenhum recurso pago pode ser implantado.
- Diretriz do projeto: 50 RPS isoladamente não justifica Kubernetes; Compose é só local.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **A** Topologia-alvo **agnóstica** (containers + PostgreSQL gerenciado + broker + LB/WAF + IdP + observabilidade), com **um** provedor usado apenas como exemplo de dimensionamento/custo | Não compromete com fornecedor sem requisito; permite estimar custo com fonte | Exemplo pode ser lido como escolha; mitigado por rótulo explícito |
| **B** Kubernetes gerenciado | Padrão de mercado para muitos times | Operação e custo desproporcionais a 3 processos e 50 RPS; sem requisito |
| **C** PaaS específico (Container Apps, App Runner, Cloud Run) | Menor operação | Compromisso com fornecedor; detalhes de rede/mTLS variam |
| **D** Não especificar | Zero esforço | Impede custo e HA; falha A-17 |

## Decisão recomendada

Opção **A**:

- **Protótipo**: Docker Compose com redes `edge`, `app`, `data`; só o gateway exposto; healthchecks; job one-shot de migrações. Não é HA.
- **Alvo agnóstico**: borda (WAF/LB/TLS) → gateway (≥ 2 réplicas) → APIs e worker em containers gerenciados (≥ 2 réplicas para APIs, 1–N worker) → PostgreSQL gerenciado multi-zona com backup/PITR → broker em cluster (3 nós) → IdP corporativo → secrets/PKI → coletor OTel + backends.
- **Exemplo concreto só para custo** (HIPÓTESE, não escolha): AWS, porque cada componente tem serviço gerenciado equivalente (ECS Fargate, RDS PostgreSQL, Amazon MQ for RabbitMQ, ALB + WAF, Cognito ou IdP externo, CloudWatch, Secrets Manager). Preços **não** são afirmados sem fonte e data (`cost-estimate.md`).
- **Arquitetura de transição (A-16)**: não há sistema legado; portanto não há transição no sentido clássico. Em seu lugar, um caminho de evolução em estágios (E0 protótipo → E1 alvo pequeno → E2 bancos separados/réplica de leitura) sem mudança de código entre E1 e E2 graças aos dois bancos (ADR-0001).
- **Rollout/rollback**: imagens imutáveis por SHA; migrações expand-contract executadas antes do deploy; rolling/blue-green com readiness; rollback de aplicação sem rollback destrutivo de banco; eventos versionados (`version`, routing key) com consumidores tolerantes implantados antes dos produtores.
- **RTO/RPO**: não inventar; declarar por componente o que depende do provedor (RDS multi-AZ, backups) e o que a arquitetura garante (projeção reconstruível a partir da fonte; outbox republicável).

## Consequências e trade-offs

- A estimativa de custo fica em dimensionamento + páginas de preço a consultar + faixa de ordem de grandeza rotulada como hipótese; valores só em F6 com fonte e data.
- Kubernetes permanece possível como FUTURO se o time/ambiente já o operar.
- Compose local não demonstra HA; a defesa deve dizer isso.

## Validação e evidências previstas

- T-12: revisão de deploy/migration/contratos; plano de rollback; compatibilidade de eventos.
- F6: Compose sobe com healthchecks verdes (evidência real), telemetria básica visível.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
