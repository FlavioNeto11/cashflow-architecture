# Requisitos não funcionais — metas e método (refinamento F0)

`EX`: enunciado; `ORIENT`: orientação adicional; `PROP`: meta sugerida sujeita a aprovação e benchmark. Os status desta tabela são os de F0/F1 (**PROPOSTO**). **Atualização F8 (2026-10-29):** parte das metas foi medida na stack local em F7 (um host, Docker Desktop); o estado de cada uma, com evidência e ressalvas, está em `traceability.csv` e `docs/architecture/implemented.md`. Nenhuma medição vale para produção.

| ID | Origem | Necessidade | SLI / observável | SLO / regra | Evidência prevista |
|---|---|---|---|---|---|
| RNF-01 | EX | Independência Lançamentos/Consolidado | Taxa de POSTs de lançamento bem-sucedidos durante a falha de **qualquer componente exclusivo do consolidado** (enumeração definida em F1, conforme DEC-03; sem presumir topologia nem stores separados) | Mesma taxa de sucesso de POST observada sem a falha (sem erro atribuível ao consolidado); lançamentos feitos durante a falha aparecem no consolidado após o retorno | T-04, T-05 |
| RNF-02 | EX | Pico de consulta | Requisições/s efetivamente enviadas e atendidas no Consolidado | 50 RPS sustentadas por duração declarada; ensaio com `dropped_iterations > 0` é inválido | T-08 (k6 `constant-arrival-rate`, duração e massa declaradas) |
| RNF-03 | EX | Perda de requisições | `(5xx + 429 + timeouts do cliente + erros de conexão) / requisições enviadas pelo k6` — interpretação PROP (DEC-09) | ≤ 5% durante o pico; interpretação deve ser aprovada. Ensaio-base em estado saudável; execuções degradadas (T-04/T-05 concomitantes) reportadas à parte. Condições para 429 e método em "Métricas calculadas" | T-08, logs do gateway como verificação cruzada |
| RNF-04 | ORIENT | Segurança corporativa | Respostas a token ausente, adulterado, expirado, sem escopo ou de outro tenant | 100% negadas (401/403/404 conforme política) nos casos cobertos; controles documentados e testados conforme implementação | T-07, testes de TLS/mTLS quando implementados |
| RNF-05 | ORIENT | Observabilidade | Logs estruturados com correlation-id, métricas (taxa de erro, latência, lag de consolidação, fila/outbox pendente), traces ponta a ponta | Diagnóstico de erro, lag e fila possível sem acesso ao código | T-11, demonstração |
| RNF-06 | ORIENT | Deployment | Topologia + estratégia + rollback | Documentado e compatível com arquitetura-alvo; migrations retrocompatíveis | T-12, revisão arquitetural |
| RNF-07 | PROP | Integridade | Lançamentos confirmados (P-07 de `RF.md`) ausentes na fonte ou nunca aplicados à projeção após recuperação | **Zero**; independente dos 5% de RNF-03 | T-05, T-10, reconciliação (RF-10) |
| RNF-08 | PROP | Idempotência | Efeitos duplicados para mesma chave/evento | **Zero** nos cenários cobertos (retry HTTP, redelivery, concorrência) | T-03, T-06, T-10 |
| RNF-09 | PROP | Latência consulta | p95 / p99 sob 50 RPS | p95 ≤ 300 ms, p99 ≤ 1 s, **apenas meta preliminar**; timeout do cliente proposto: 2 s | T-08, ambiente especificado |
| RNF-10 | PROP | Sucesso sob carga | 2xx com valor correto / requisições enviadas | ≥ 99%, **meta mais estrita sugerida**, distinta de RNF-03 | T-08 sustentado 10 min (duração sugerida) |
| RNF-11 | PROP | Disponibilidade produtiva | Sucesso rolling 30 dias | 99,9%, **não fornecido pelo avaliador**; não validável no desafio | T-12 (prontidão operacional), monitoramento produtivo hipotético |
| RNF-12 | PROP | Recuperabilidade | RTO/RPO por componente | Pendentes, não inventar números | T-05, T-12, runbook e exercícios |

## Métricas calculadas

- **Taxa de perda (RNF-03, PROP):** numerador = 5xx + 429 + timeouts do cliente + erros de conexão (recusada/reset/DNS/TLS); denominador = `http_reqs` do k6. 4xx de cenários negativos deliberados (400/401/403/404/409/422) ficam fora do numerador e são reportados à parte; em carga só com requisições válidas, qualquer 4xx é anomalia.
- **Condições para 429 contar como perda (PROP):** (1) perfil de carga declarado (número de merchants/tokens distintos usados no ensaio); (2) rate limit do gateway dimensionado para admitir pelo menos esse perfil a 50 RPS; (3) o ensaio não concentra toda a carga em um único token sujeito a limite por token. Sem essas condições, 429 mede a configuração do ensaio, não a capacidade do consolidado.
- **Gerador de carga (PROP):** `maxVUs ≥ 50 × timeout(s)`; com timeout de 2 s o piso é 100, sugestão 150–200 como folga. Ensaio-base de RNF-03 em estado saudável; execuções degradadas (T-04/T-05 concomitantes) são reportadas à parte e não substituem o ensaio-base.
- **Nota de método (para F7):** `http_req_failed` do k6 conta 4xx como falha por padrão; F7 precisa configurar `expectedStatuses` (ou um Counter próprio) para separar 429 dos demais 4xx e aplicar a fórmula acima.
- **Timeout do cliente:** proposta principal 2 s (RNF-09). Alternativa: cliente com 10 s contando `duration > 2 s` como perda, permitindo, em uma única execução, leitura estrita (`> 2 s` = perda) e leniente (só timeout de 10 s = perda).
- **Correção:** resposta 2xx com valor monetário divergente do oráculo reprova o ensaio; verificar consistência numérica além de HTTP 200.
- **Throughput:** reportar requisições enviadas, atendidas e `dropped_iterations`; preferir `constant-arrival-rate` com VUs suficientes.
- **Lag de eventos:** tempo desde o commit do lançamento até a aplicação na projeção.
- **Escopo:** RNF-02/03/09/10 referem-se à leitura do consolidado. O registro de lançamentos segue RNF-01/07/08.

**Proibido:** converter o máximo de 5% de falhas de consulta em permissão para perder dados financeiros. Perda de requisição HTTP ≠ perda financeira.
