# ADR-0009 — Interpretação de "no máximo 5% de perda de requisições" e método do ensaio de 50 RPS

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a DEC-09, RNF-02, RNF-03, RNF-09, RNF-10.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- Enunciado (EX): consolidado recebe 50 requisições/s em picos com no máximo 5% de perda de requisições.
- "Perda" não está definida. O projeto não admite interpretá-la como perda financeira; RNF-07 exige zero lançamentos confirmados perdidos.
- Gateway com rate limit pode produzir `429`; sem regra, o ensaio poderia mascarar ou inflar a perda.
- k6 não está instalado no host; fallback em container a verificar em F7.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **A** Perda = `(5xx + 429 + timeouts do cliente + erros de conexão) / http_reqs` do k6, com `constant-arrival-rate` a 50 it/s e `dropped_iterations = 0` como validade | Conservadora (conta tudo que o cliente não recebeu com sucesso); mensurável; independente de logs do servidor | Depende de configurar `expectedStatuses`/Counter para separar 429 de 4xx deliberados |
| **B** Denominador = requisições recebidas pelo servidor (logs do gateway) | Isola a aplicação do gerador | Não conta conexões recusadas/timeouts antes do servidor; otimista; só como verificação cruzada |
| **C** Excluir 429 do numerador | Rate limit "não é falha" | Permite cumprir 5% descartando carga; mascara capacidade real; descartada |
| **D** Perda = apenas 5xx | Simples | Ignora timeouts (o cliente não recebeu resposta); descartada |

## Decisão recomendada

Opção **A**, com as condições abaixo (todas PROP até aprovação):

- **Fórmula:** `taxa_perda = (5xx + 429 + timeouts do cliente + erros de conexão [recusada/reset/DNS/TLS]) / http_reqs`.
- **Executor:** `constant-arrival-rate`, 50 iterações/s, 1 requisição por iteração; `preAllocatedVUs` 100, `maxVUs` 150–200 (`≥ 50 × timeout`). `dropped_iterations > 0` → ensaio **inválido** para RNF-02 (repetir com mais VUs).
- **Timeout do cliente:** 2 s (coerente com p99 ≤ 1 s de RNF-09). Alternativa em uma execução: timeout 10 s e contar `duration > 2 s` como perda estrita.
- **Perfil de carga declarado:** 10 merchants/tokens distintos em round-robin (5 RPS cada) sobre datas com massa conhecida; variante de pior caso com **um único token a 50 RPS**, também reportada.
- **Rate limit dimensionado para o perfil** (números em `security.md`, HIPÓTESES): rota de consulta global 120 req/s, por merchant 60 req/s — admite tanto o perfil de 10 tokens quanto o pior caso de um token a 50 RPS. Assim, `429` no ensaio mede o sistema, não a configuração.
- **Ensaio-base em estado saudável** (sem falhas injetadas), duração sugerida 10 min, massa declarada. Execuções com T-04/T-05 concomitantes são reportadas à parte.
- **4xx legítimos** de cenários negativos deliberados ficam fora do numerador e são reportados; em carga só válida, qualquer 4xx é anomalia.
- **Correção:** resposta `2xx` com valor divergente do oráculo reprova o ensaio (check de exatidão no k6).
- **Metas separadas:** RNF-03 `taxa_perda ≤ 5%` (EX); RNF-10 `sucesso ≥ 99%` (PROP); RNF-09 p95 ≤ 300 ms, p99 ≤ 1 s (PROP, preliminares). Reportar também com `include=closingBalance` em série separada.
- **Verificação cruzada:** contagem no gateway (opção B) reportada ao lado, nunca como métrica principal.

## Consequências e trade-offs

- Perda HTTP de leitura **não** é perda financeira: o `POST` não participa do ensaio de RNF-03 e é governado por RNF-07/08.
- 50 RPS em laboratório prova capacidade naquele ambiente e perfil, não SLA produtivo.
- Separar 429 exige configuração explícita do k6 (`http_req_failed` conta 4xx por padrão); nota de método para F7.

## Validação e evidências previstas

- T-08 com script k6 versionado, `summary` JSON, `http_reqs`, `dropped_iterations`, distribuição de status, p95/p99, checks de exatidão; ambiente e massa registrados em `evidence-log.md`.
- Gap de ferramenta: k6 em container (`grafana/k6`) a verificar em F7.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
