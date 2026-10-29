# Plano de testes (estado após F9)

Substitui o plano "proposto (não implementado)" anterior. Fonte das linhas T-xx: plano de testes por risco definido em F1 (`docs/architecture/target.md`). Resultado das execuções: `docs/testing/evidence-log.md` (seções F7 e F9), `docs/testing/evidence/f7/` e `docs/testing/evidence/f9/` (reexecuções com as imagens atuais e correções depois do G2).

## Legenda de status

- **VALIDADO (stack)**: executado nesta rodada contra a stack Compose BASE real, com log reproduzível em `docs/testing/evidence/f7/` terminando em `exit=`.
- **IMPLEMENTADO (suíte)**: existe teste .NET (Testcontainers/in-process) que passou na rodada de 352 testes do Passo B (`21-02-suite.log`: Persistence 49, Transactions.Api 169, Consolidation 66, Security 68; 0 falhas, 0 ignorados, com as correções PF-03 e P1-1 no código; a rodada anterior de 339 está em `13-fixtures-ports-DEPOIS.log`). O mapeamento teste -> T-xx foi feito por F7 a partir de NOMES de classes/métodos e comentários de cabeçalho, sem revisar cada corpo; a suíte passar não prova que o corpo cobre integralmente o T-xx. Em F9 a suíte passou a 364 testes (Transactions.Api 175, Consolidation 71, Security 69, Persistence 49; `f9/06-suite-transactions-api.log` a `f9/09-suite-persistence.log`) e roda também no CI (`f9/21-ci-github-actions.log`).
- **PARCIAL**: parte do requisito ou do cenário foi exercitada na stack; o texto da linha diz o que ficou de fora, inclusive desvios do ADR-0009.
- **OBSERVADO (sem asserção)**: o roteiro imprime o comportamento mas não falha se ele mudar; não conta como validação.
- **NÃO COBERTO**: não executado, com o motivo.
- Um mesmo T-xx pode ter duas marcas (suíte + stack). Falha de ferramenta ou de ambiente conta como PENDENTE/NÃO COBERTO, nunca como passou.

Ambiente de todas as execuções de stack: Docker Desktop (Windows, WSL2), 6 CPUs, cerca de 7,8 GiB, um host. Linux nativo NÃO foi validado. Scripts e logs em `docs/testing/evidence/f7/` (caminho abreviado como `f7/` abaixo).

## Matriz T-xx

| T | Cenário | Arquivo / script | Status | Evidência e ressalvas |
|---|---|---|---|---|
| T-01 | Montante errado (100,00 - 35,20 = 64,80) | suíte `Transactions.Api.Tests/RegistrationTests`, `DomainTests` | IMPLEMENTADO (suíte) | `f7/13-fixtures-ports-DEPOIS.log`. Oráculo por SQL na fonte também em `f7/03-*.log` e `f7/07-*.log` (VALIDADO stack para valores usados nesses roteiros, não o 64,80 literal) |
| T-02 | Consolidado incorreto, ponta a ponta | `f7/05-k6-run.sh` + `f7/f7-t08.js` (corpo de cada GET comparado ao oráculo SQL); `f7/07-resilience.sh` (consolidado = fonte) | VALIDADO (stack) | `f7/06-t08-*.log`: 6000/6000 e 30001/30001 corpos corretos; `f7/07-resilience.log` |
| T-03 | Dupla contabilização (retry HTTP + redelivery) | `f7/03-idempotency-concurrency.sh`; `f7/08-dlq-redelivery.sh` D2; suíte `IdempotencyTests`, `ConsolidationWorkerTests` | VALIDADO (stack) + IMPLEMENTADO (suíte) | `f7/03-idempotency-concurrency.log` (13 OK, 0 FALHA) para retry HTTP e concorrência. Redelivery: a D2 do `f7/08-dlq-redelivery.log` NÃO provava consumo; só a reexecução `f7/08-d2-redelivery-reexec.log` prova (5 publicações `routed:true`, 5 linhas "Reentrega ... sem efeito" no worker, projeção `124.68 0.00 3` e Inbox 3 inalterados, DLQ 0, `FALHAS=0`). Sem a reexecução, a parte de redelivery seria PARCIAL Falha ambígua do CLIENTE reproduzida no Passo B: `f7/21-06-ambiguous-failure.log` (45 tentativas com aborto após o envio; 24 já persistidas com retry 201 `Idempotency-Replayed: true`, 21 não persistidas com retry 201 sem o cabeçalho; sempre 1 linha em `transactions`, `idempotency_keys` e `outbox_messages`). Em F9, `03` reexecutado com as imagens atuais (`f9/16-rerun-03-idempotency.log`, 0 FALHA) e SIGKILL do `07` reexecutado (`f9/17-rerun-07-resilience.log` R3, R4a e R4b: sem perda nem duplicata) |
| T-04 | Acoplamento de disponibilidade | `f7/07-resilience.sh` R1, R3, R4c | VALIDADO (stack) | R1: worker + consolidation-api parados, 200 POSTs todos 201, convergência após restart. Banco compartilhado: ver T-04c. F9: R1 reexecutado com as imagens atuais, 200 POSTs todos 201 (`f9/17-rerun-07-resilience.log`) |
| T-04c | Só o consolidation_db indisponível | nenhum | NÃO COBERTO | exigiria `ALTER DATABASE ... ALLOW_CONNECTIONS false` na stack real; não executado. Suíte `ConsolidationWorkerTests.DatabaseDown_PausesWithoutAckOrDlq_ThenAppliesEachEventOnce` cobre o worker, IMPLEMENTADO (suíte) |
| T-05 | Perda com broker indisponível | `f7/07-resilience.sh` R2; suíte `OutboxPublisherTests`, `EndToEndRecoveryTests`, `ConsolidationWorkerTests.BrokerDown*` | VALIDADO (stack) + IMPLEMENTADO (suíte) | R2: rabbitmq parado, 300 POSTs todos 201, pendência na outbox chegou a 76 durante a queda e foi a 0 13,6 s após o broker ser registrado como saudável, tentativas máx. 5. Nenhuma perda. F9: R2 reexecutado com as imagens atuais, 300 POSTs todos 201, 300 linhas e 300 no Inbox (`f9/17-rerun-07-resilience.log`) |
| T-05b | Broker pausado (`compose pause`) | nenhum | NÃO COBERTO | não executado nesta rodada |
| T-06 | Corrida / lost update | `f7/03-idempotency-concurrency.sh` seção 4 (40 POSTs simultâneos, mesmo merchant/data); suíte `ConsolidationWorkerTests.T09_*` | VALIDADO (stack) + IMPLEMENTADO (suíte) | fonte = 40,00 = projeção |
| T-07 | Acesso indevido (401/403/404) | `f7/02-security-scenarios.sh`; `f7/21-07-5xx-headers.sh`; suíte `Security.Tests` (`TokenValidationTests`, `TenantIsolationTests`, `AuthorizationTests`) | VALIDADO (stack) | `f7/02-security-scenarios.log`: 87 OK, 0 FALHA (cabeçalhos de segurança em 200, 201, 404, 422, 401 e 5xx), de ANTES da reconstrução das imagens do Passo B; o gerador de 5xx da seção 7 (NUL em `description`) deixou de produzir 500 com PF-03. Cabeçalhos em 5xx refeitos com um 502 real do gateway (consolidation-api parada): `f7/21-07-5xx-headers.log`, 11 OK, FALHAS=0. F9: `02` completo reexecutado com as imagens atuais (`f9/15b-rerun-02-security.log`, 87 OK, 0 FALHA), com a verificação da seção 7 trocada para o 422 esperado desde PF-03; a execução com a expectativa antiga está em `f9/15a-rerun-02-security-expectativa-pf03-antiga.log` (1 FALHA: 422 no lugar de 500) |
| T-08 | 50 RPS no consolidado via gateway | `f7/04-k6-seed.sh`, `f7/05-k6-run.sh`, `f7/06-t08-all.sh`, `f7/f7-t08.js`; `f9/14-t08-10tenants.sh`, `f9/f9-t08-10t.js` | VALIDADO (stack, 10 tenants em F9) | ver "T-08: números medidos". Desvios do ADR-0009: 2 tenants e 1 data em vez de 10 tenants em round-robin; carga uniforme, sem rajada; sem contagem do lado do gateway (contagens do k6). k6 no mesmo host, limites C-F5-08 inalterados. O que ficou VALIDADO nessas condições: 0,00 por cento de perda a 50 RPS e corpo correto em 100 por cento das respostas 200. F9: perfil de 10 tenants em round-robin (`f9/14-t08-10tenants.log`): 6001 req a 50,01/s, 0,00% de perda, p95 7,09 ms, p99 10,82 ms, 600 ou 601 respostas corretas por merchant contra o oráculo SQL de cada um. Seguem os desvios de 1 data, carga uniforme e k6 no mesmo host |
| T-08+POST | T-08 com POSTs concorrentes | `f7/06-t08-all.sh` rodada `get-single-plus-post` | PARCIAL (stack) | GET 50/s + POST 10/s: GET 0 por cento de perda; POST 1201 x 201 (mesmos desvios do T-08). A linha de base de POST (rodada `post-baseline`) é confundida por cold start e não prova "igual à linha de base" |
| T-09 | Retroativo em data já consolidada | suíte `RegistrationTests` (retroativo), `ConsolidationWorkerTests`, `ReconToolTests` | IMPLEMENTADO (suíte) | sem ensaio retroativo dedicado na stack; `f7/07-*.log` usa datas passadas, mas não é um teste de retroatividade sobre dia já consolidado |
| T-10 | Falha após commit, antes do ACK | suíte `ConsolidationWorkerTests` (queda após commit antes do ack, T10 duplicata); `f7/07-resilience.sh` R3 (SIGKILL do worker) | IMPLEMENTADO (suíte) + VALIDADO (stack) parcial | R3: janela NÃO controlada (o kill cai em ponto arbitrário); convergiu e Inbox exato. A janela exata commit/ack só é provada pela suíte |
| T-11 | Visibilidade de lag | nenhum de F7 | NÃO COBERTO | exige métricas de lag/idade (src); `traceparent` nas mensagens AMQP foi observado em `f7/07-resilience.log` |
| T-12 | Rollout / rollback | n/a | NÃO COBERTO por F7 | revisão arquitetural; suíte `Persistence.Tests/MigrationTests` (IMPLEMENTADO) cobre migrações |
| T-13 | Payload inválido / DLQ | `f7/08-dlq-redelivery.sh` D1; suíte `ConsolidationWorkerTests` (DLQ), `TopologyTests` | VALIDADO (stack) parcial + IMPLEMENTADO (suíte) | D1: JSON malformado -> DLQ com `x-reason=malformed_json`. Item (B) (banco fora por 30 s com fila cheia) NÃO executado na stack |
| T-14a | Reconciliação x evento em trânsito | suíte `EndToEndRecoveryTests` | IMPLEMENTADO (suíte) | não repetido na stack; `f7/08-dlq-redelivery.log` D3 faz reconcile após redrive (sem divergências nessa execução; vale só para D3) |
| T-14b | Reconciliação contínua com fluxo ativo | nenhum | NÃO COBERTO | não executado |
| T-15 | Publicação sem rota | suíte `OutboxPublisherTests.T15_*`, `ConfirmedPublish_RoutesToMainQueue_AndUnroutableIsReturned` | IMPLEMENTADO (suíte) | não repetido na stack |
| T-16 | Subida sem dependências | nenhum de F7 | NÃO COBERTO | não executado (R2 parou o broker com a API no ar, o que é distinto de subir sem broker) |
| T-17 | mTLS (negativos) | `f7/02-security-scenarios.sh` seção 9; suíte `MutualTlsTests` (6) | VALIDADO (stack) parcial + IMPLEMENTADO (suíte) | seção 9 agora ASSERTIVA, em transactions-api e consolidation-api: sem certificado de cliente, certificado de CA estranha e HTTP puro são recusados (http=000, curl 35 ou 56 ou 52); controle positivo com `gateway-client` chega à aplicação (404 de rota inexistente). Cert expirado, SAN/EKU errados e blocklist ficam só na suíte |
| T-18 | Issuer/IdP | suíte `TokenValidationTests`, `IdpOutageTests` | IMPLEMENTADO (suíte) | não repetido na stack |
| T-19 | Esgotamento de conexões | suíte `Persistence.Tests/ConnectionBudgetTests` (orçamento estático) | NÃO COBERTO (saturação real); orçamento estático IMPLEMENTADO | sem saturação de pool na stack |
| T-20 | Redrive da DLQ | `f7/08-dlq-redelivery.sh` D3, D4; suíte `ReconToolTests` | VALIDADO (stack) parcial + IMPLEMENTADO (suíte) | D3: evento não aplicado via redrive foi aplicado 1 vez (112,34 / 2, Inbox 2). A entrada na DLQ nesse teste foi SIMULADA por `reject_requeue_false` na API de gestão, não por rejeição do worker. `redrive --all` sai com 2 quando restam mensagens recusadas (malformada). Mensagem já aplicada: dedupe provada por redelivery direto (reexecução da D2, `f7/08-d2-redelivery-reexec.log`), não por redrive. Só o CAMINHO FELIZ do redrive foi exercitado; falha do próprio redrive (broker fora, republicação parcial) NÃO foi. A leitura de `redrive --all` = 2 é código de saída da ferramenta; o `rc=1` do `07-resilience.log` é `RestartCount`, não código de retorno |
| T-21 | Concorrência da mesma Idempotency-Key | `f7/03-idempotency-concurrency.sh` seções 2, 3 e 5; `f7/f7_race.py`; suíte `IdempotencyTests.TwentyParallelPosts_*`, `TwentyParallelReversals_*` | VALIDADO (stack) + IMPLEMENTADO (suíte) | 30 POSTs simultâneos mesma chave: 1 linha, 1 evento; 20 estornos mesma chave: 1; mais 20 com chaves diferentes: ainda 1 |
| T-22 | Estorno antes do original na projeção | nenhum nome de teste mapeado por F7 | NÃO COBERTO (não verificado) | F7 não localizou teste dedicado pelo nome; não afirmar cobertura |
| T-23 | Reconstrução além da retenção | suíte `ReconToolTests` (`Reconcile_OutOfRetention_*`, `Rebuild_*`) | IMPLEMENTADO (suíte) | `f7/09-inherited-checks.log` I3: rebuild de data vazia exit 0; limites de argumentos exit 64 |
| T-24 | Paginação | suíte `RegistrationTests.List_FiltersByDate_AndPaginates_AndRejectsPageSizeOver200`; `f7/09-inherited-checks.sh` I1 | IMPLEMENTADO (suíte) + VALIDADO (stack) parcial | I1: pageSize 200 aceito, 201/0/abc dão 422. Ordem e soma = RF-03: só suíte |
| T-25 | Tenant (negativos) | `f7/02-security-scenarios.sh` seções 3 e 4; suíte `TenantIsolationTests`, `CrossTenant_*` | VALIDADO (stack) parcial + IMPLEMENTADO (suíte) | NÃO coberto: edição do atributo `merchant_id` no Keycloak; `ops_operator` |
| T-26 | Token (negativos) | `f7/02-security-scenarios.sh` seções 1 e 0; suíte `TokenValidationTests`, `TokenClockAndKeyRotationTests` | VALIDADO (stack) parcial + IMPLEMENTADO (suíte) | alg none, HS256, kid desconhecido e rajada de 40 tokens: só códigos de status. No HS256 o roteiro usa o PEM da CA como segredo HMAC (ataque de confusão de algoritmo simulado); ele é recusado porque a lista de algoritmos aceitos é só RS256 e ES256 (`JwtBearerSettings.cs:40`). A contagem de buscas ao JWKS NÃO foi medida |
| T-27 | Rate limit | `f7/06-t08-all.sh` (1 token, 2 tokens, 10 min com renovação de token), `f7/09-inherited-checks.sh` I5, `f7/21-04-p1-1.sh` (c, a1, a2, b1, b2) | VALIDADO (stack) para (a) e (c); (b) VALIDADO depois da correção de F9 | Ensaio-base: 429 = 0, token_fetches=400 em 10 min. P1-1 (com override OTLP temporário): (c) 50 RPS, 3001 req, 0 x 429, `rate_limited_total` delta 0; (a) flood anônimo de 200/s e 600/s com A a 50 RPS: 0 x 429, perda 0,00%, p95 7,83 e 10,01 ms, p99 13,98 e 19,68 ms, anônimos recusados 16182 e 52183 (`f7/21-04-a1.log`, `a2.log`); (b) rajada de A acima de 60 req em < 1 s com B a 20 req/s: perda de B 0,10%, 0,00%, 0,00% (rajada de 90) e 0,90% (rajada de 200), 1 de 3 execuções com 429 para B no b1 e `global_limiter` +1 e +302: oráculo de perda (até 5%) cumprido, oráculo estrito de 0 x 429 para o outro merchant NÃO cumprido (PF-18). F9, com os limitadores encadeados: `f9/20-pf18-stack-b1.log` (rajada de 90) e `f9/20-pf18-stack-b2.log` (rajada de 200), B com 0 x 429 em 1001 e 1000 GETs enquanto A recebia 83 e 371 recusas; teste determinístico `MerchantBurstGlobalLimiterTests` (`f9/02-pf18-antes.log` falha, `f9/04-pf18-depois.log` passa) |
| T-28 | Listener de saúde x mTLS | `f7/02-security-scenarios.sh` seção 9; suíte `DualListenerTests` | VALIDADO (stack, asserções) + IMPLEMENTADO (suíte) | seção 9 assertiva em transactions-api, consolidation-api, worker e gateway: 8081 só `/health/*` (case-insensitive: `/HEALTH/LIVE` 200; `/health/live/` 404; `?x=1` 200; rota de negócio 404). Antes desta correção as seções eram só observação |
| T-29 | Injeção via privilégios operacionais | `f7/09-inherited-checks.sh` I2 (grants); suíte `Persistence.Tests` (`ConsolidationGrantsTests`, `ReconRoGrantsTests`) | NÃO COBERTO (publicação AMQP `operations` / escrita `recon_writer` fora de execução registrada); grants IMPLEMENTADO/VALIDADO | I2 só lista privilégios SQL; não tentou publicar como `operations` |
| T-30 | Perfil de carga declarado | `f7/f7-t08.js`, `f7/06-t08-*.log`; `f9/f9-t08-10t.js` | VALIDADO (stack) com 10 tenants em F9 | `dropped_iterations` 0 e 2 tenants declarados. VUs: `vus_max` do k6 mede VUs INICIALIZADOS (valores 200, 240, 40 e 100 nos JSON), não o `maxVUs` configurado; o "pico 1 a 3" é a métrica `vus` amostrada e conflita com as 100 buscas de token por sondagem (cache por VU), o que indica cerca de 100 VUs em uso (inferência). Desvios do ADR-0009: 2 tenants (não 10 em round-robin), 1 data, carga uniforme sem rajada, sem contagem do lado do gateway. F9 (`f9/14-t08-10tenants.log`): 10 tenants em round-robin, `dropped_iterations` 0, 215 buscas de token em todas as fases, 0 falhas de token; seguem 1 data e carga sem rajada |
| T-31 | `merchant_id` não editável pelo usuário (Keycloak User Profile) | `f7/22-keycloak-profile.sh` | PARCIAL (stack) | API de administração (GET, credencial do `.env` não impressa): `merchant_id.permissions.edit == ["admin"]` (`f7/22-keycloak-profile.log`). Verifica a CONFIGURAÇÃO; a tentativa de edição pelo próprio usuário (fluxo interativo) NÃO foi feita |
| T-32 | IdP (Keycloak) indisponível com a stack de pé | `f7/23-idp-outage.sh` | VALIDADO (stack), janela curta | `compose stop keycloak` por cerca de 40 s: token já emitido segue dando 201 e 200 no gateway (em +3 s e +35 s), token novo falha (http=000), sem token continua 401, gateway/APIs/worker seguem prontos; após `compose start` o Keycloak fica healthy em 13 s e o fluxo volta. NÃO coberto: indisponibilidade acima de 5 min (expiração do token), reinício do gateway durante a queda, expiração do cache de metadados (`f7/23-idp-outage.log`; a primeira execução, invalidada por rc=127 na sonda de prontidão, está em `f7/23-idp-outage-run1-prontidao-invalida-rc127.log`) |
| PF-03 | `description` com caractere de controle (Cc) deve dar 422 | `f7/15-extra-probes.sh` X1; `f7/21-03b-pf03-controles.sh` | VALIDADO (stack, `description`); `reason` do estorno NÃO COBERTO | `f7/21-03-x1-pf03-stack.log`; `f7/21-03b-pf03-controles.log`: 10 Cc dão 422, 4 controles positivos dão 201, 4 linhas e 4 mensagens de outbox. Achado novo PF-17: UTF-8 inválido dava 500 (corrigido em F9, linha PF-17) |
| PF-14 | Falha ambígua de HTTP (cliente perde a resposta) | `f7/21-06-ambiguous-failure.sh` | VALIDADO (retry de cliente em F7; SIGKILL do `07` em F9) | `f7/21-06-ambiguous-failure.log`: 45 tentativas, 24 persistidas antes do retry (201 com `Idempotency-Replayed: true`) e 21 não persistidas; sempre 1 linha, 1 chave de idempotência e 1 mensagem de outbox. SIGKILL: `f9/17-rerun-07-resilience.log` R3 (worker), R4a (transactions-api: 297 x 201 e 3 x 502, reenvio com a mesma chave fecha 300 linhas) e R4b (gateway), sem perda nem duplicata |
| PF-17 | Byte UTF-8 inválido no corpo do POST | suíte (`f9/01-pf17-antes.log` e `f9/03-pf17-depois.log`); `f7/21-07-5xx-headers.sh` | VALIDADO (suíte + stack, F9) | Antes: 500 (4 de 6 testes falhavam); depois: 400 (6 de 6). Na stack, `f9/19-rerun-21-07-pf17-stack.log` recebe 400; o roteiro de F7 ainda espera 500 e marca 1 FALHA, que é a própria prova da correção |
| PF-18 | Rajada de um merchant não pode gerar 429 para outro | suíte `MerchantBurstGlobalLimiterTests`; `f7/21-04-p1-1.sh` b1/b2 | VALIDADO (suíte + stack, F9) | Ver T-27. Rótulo de métrica: com os limitadores encadeados, `aspnetcore.rate_limiting.requests{result=global_limiter}` passa a contar também as recusas por merchant; o contador que separa a origem é `rate_limited_total{route}` |
| PF-09 | Purge da DLQ com trilha | suíte `ReconToolTests` (`f9/05-pf09-purge.log`, 29 testes); `f7/08-dlq-redelivery.sh` D5 | VALIDADO (suíte + stack, F9) | `recon-tool dlq purge (--all \| --id)` com `--reason` obrigatório grava uma linha `dlq_purge` em `reconciliation_runs` antes do ACK; D5: DLQ 3 → 2 (`--id`) → 0 (`--all`), 3 trilhas (`f9/18-rerun-08-dlq.log`) |
| Recursos | `docker stats` denso durante o k6 (inclui k6, worker, rabbitmq) | `f7/05-k6-run.sh`; `f7/21-05-stats-summary.py` | VALIDADO como medição; PARCIAL como análise de capacidade | `f7/21-05-stats-summary.log` (amostra a cada cerca de 3,3 s; BASE GET 50/s: gateway CPU mediana 15,43% e pico 100,42%, 117,3 MiB; worker mediana 0,04%; rabbitmq mediana 0,29% e pico 179,40%). Sem relação com produção |
| Rebuild | Imagens do gateway e da transactions-api reconstruídas com as correções | `f7/21-01-rebuild.log` | VALIDADO | `sha256:aea4b28e0c3c` e `sha256:7ee170af2fba`, 7/7 healthy |

## T-08: números medidos

Condições: k6 (`grafana/k6@sha256:3e4186aea62e1...`) em container na rede `cashflow_app`, alvo `https://gateway:8080` (TLS de borda, JWT, mTLS aos serviços), `constant-arrival-rate`, timeout de 2 s, aquecimento descartado (20 s; 10 s no POST), 2 tenants (não 10), data 2026-02-10. Perda por ADR-0009 = (5xx + 429 + timeouts + erros de conexão) / requisições.

| Rodada | Janela | Requisições | Taxa | Perda | 429 | 5xx | p50 | p95 | p99 | Log |
|---|---|---|---|---|---|---|---|---|---|---|
| GET, 1 token | 120 s | 6000 | 50,00/s | 0,00 por cento | 0 | 0 | 3,97 ms | 6,11 ms | 8,56 ms | `f7/06-t08-get-single.log` |
| GET, 2 tokens | 120 s | 6001 | 50,01/s | 0,00 por cento | 0 | 0 | 3,95 ms | 6,06 ms | 8,83 ms | `f7/06-t08-get-two-tokens.log` |
| GET + POST 10/s | 120 s | GET 6000, POST 1201 | 50,00/s e 10,01/s | 0,00 por cento | 0 | 0 | GET 3,95 / POST 13,11 | GET 6,22 / POST 19,72 | GET 8,93 / POST 23,98 | `f7/06-t08-get-single-plus-post.log` |
| GET com saldo de fechamento | 120 s | 6000 | 50,00/s | 0,00 por cento | 0 | 0 | 4,12 ms | 6,12 ms | 8,29 ms | `f7/06-t08-get-closing.log` |
| GET, 10 min | 600 s | 30001 | 50,00/s | 0,00 por cento | 0 | 0 | 3,97 ms | 6,05 ms | 8,41 ms (máx. 158,14) | `f7/06-t08-get-single-10min.log` |
| Sondagem 80/s, 1 token | 30 s | 2400 | 80,00/s | 22,79 por cento | 547 | 0 | 3,51 ms | 6,09 ms | 9,69 ms | `f7/06-t08-probe-single-80.log` |
| Sondagem 130/s, 2 tokens | 30 s | 3901 | 130,03/s | 5,05 por cento | 197 | 0 | 3,60 ms | 6,39 ms | 11,40 ms | `f7/06-t08-probe-two-130.log` |
| POST 10/s (linha de base) | 120 s | 1201 | 10,01/s | 0,00 por cento (todos 201) | 0 | 0 | 13,88 ms | 31,50 ms | 80,84 ms | `f7/06-t08-post-baseline.log` |

Leitura:

- As sondagens de 80/s e 130/s ficam FORA do ensaio de 50 RPS; mostram onde os limites C-F5-08 (leitura 60/s por merchant; global 120/s) começam a gerar 429, não são resultado de capacidade do serviço.
- A linha de base de POST rodou primeiro, em cold start; seu p95 (31,50 ms) e p99 (80,84 ms) são maiores que os do POST concorrente com GET (19,72 e 23,98 ms). Portanto F7 NÃO afirma que o POST "degrada" ou "não degrada" por causa do GET; só que não houve falha e que a latência observada com GET concorrente não foi pior que a da linha de base.
- Latências são de loopback no Docker Desktop. Não representam produção.
- O isolamento do limite global (120/s) não pôde ser separado do limite por merchant com apenas 2 tenants.
- O `f7-t08.js` FOI alterado entre rodadas canônicas (12:17:58Z, durante a rodada `get-closing`): as cinco primeiras rodadas imprimem o formato antigo e as duas sondagens e a de 10 min o novo (contador `f7_token_fetches`); detalhes e limite dessa reconstrução em `docs/testing/evidence-log.md`.
- O k6 verifica o TLS de borda (`SSL_CERT_FILE=/certs/ca.crt`, `insecureSkipTLSVerify: false`). `05-k6-run.sh` agora monta só `ca.crt` (antes montava todo `infra/pki/out`, com `ca.key` e `foreign-ca.key`); as rodadas acima usaram a montagem antiga e não foram repetidas.
- `docker stats` das rodadas: `f7/19-docker-stats-summary.log`; k6, worker e rabbitmq não foram amostrados.

## Resiliência e integridade (F7)

Scripts: `f7/07-resilience.sh` (R0 a R5), `f7/08-dlq-redelivery.sh` (D1 a D5; `ONLY=D2` roda só a D2), `f7/09-inherited-checks.sh` (I1 a I6), `f7/10-certs-permissions.sh`, `f7/13-fixtures-ports.sh`, `f7/15-extra-probes.sh`, `f7/18-lock-timeout.sh`, `f7/12-trivy-imagens.sh`, `f7/17-secret-scan.sh`, `f7/22-keycloak-profile.sh`, `f7/23-idp-outage.sh`. Resultados de cada um: seção F7 de `docs/testing/evidence-log.md`. Passo B: `f7/21-01-rebuild.log`, `f7/21-02-suite.sh`, `f7/21-03b-pf03-controles.sh`, `f7/21-04-p1-1.sh` (com `21-04-all.sh`, `21-04-repeat.sh`, `21-04-load.js`, `21-04-counters.py`, `21-04-otel-override.yml`, `21-04-otelcol.yaml`), `f7/21-05-base-runs.sh`, `f7/21-05-stats-summary.py`, `f7/21-06-ambiguous-failure.sh`, `f7/21-07-5xx-headers.sh`.

## Como reproduzir

Pré-requisito: stack BASE em `infra/compose` saudável; `infra/compose/.env` presente (carregar sem imprimir: `set -a; . <(tr -d '\r' < infra/compose/.env); set +a`). Executar de Git Bash na raiz do repositório.

```
bash docs/testing/evidence/f7/02-security-scenarios.sh > docs/testing/evidence/f7/02-security-scenarios.log 2>&1
bash docs/testing/evidence/f7/03-idempotency-concurrency.sh > docs/testing/evidence/f7/03-idempotency-concurrency.log 2>&1
bash docs/testing/evidence/f7/04-k6-seed.sh > docs/testing/evidence/f7/04-k6-seed.log 2>&1
bash docs/testing/evidence/f7/06-t08-all.sh
bash docs/testing/evidence/f7/07-resilience.sh > docs/testing/evidence/f7/07-resilience.log 2>&1 ; echo "exit=$?" >> docs/testing/evidence/f7/07-resilience.log
bash docs/testing/evidence/f7/08-dlq-redelivery.sh > docs/testing/evidence/f7/08-dlq-redelivery.log 2>&1 ; echo "exit=$?" >> docs/testing/evidence/f7/08-dlq-redelivery.log
bash docs/testing/evidence/f7/09-inherited-checks.sh > docs/testing/evidence/f7/09-inherited-checks.log 2>&1 ; echo "exit=$?" >> docs/testing/evidence/f7/09-inherited-checks.log
dotnet build CashFlow.slnx -c Release -warnaserror
bash docs/testing/evidence/f7/13-fixtures-ports.sh DEPOIS-suite-completa tests/CashFlow.Persistence.Tests/CashFlow.Persistence.Tests.csproj::FullyQualifiedName~CashFlow tests/CashFlow.Transactions.Api.Tests/CashFlow.Transactions.Api.Tests.csproj::FullyQualifiedName~CashFlow tests/CashFlow.Consolidation.Tests/CashFlow.Consolidation.Tests.csproj::FullyQualifiedName~CashFlow tests/CashFlow.Security.Tests/CashFlow.Security.Tests.csproj::FullyQualifiedName~CashFlow
# Passo B (cada comando abaixo grava o próprio log; acrescente ' ; echo "exit=$?" >> log'):
bash docs/testing/evidence/f7/21-02-suite.sh > docs/testing/evidence/f7/21-02-suite.log 2>&1
bash docs/testing/evidence/f7/21-03b-pf03-controles.sh > docs/testing/evidence/f7/21-03b-pf03-controles.log 2>&1
bash docs/testing/evidence/f7/21-06-ambiguous-failure.sh 45 > docs/testing/evidence/f7/21-06-ambiguous-failure.log 2>&1
bash docs/testing/evidence/f7/21-07-5xx-headers.sh > docs/testing/evidence/f7/21-07-5xx-headers.log 2>&1   # para e religa a consolidation-api
bash docs/testing/evidence/f7/21-05-base-runs.sh > docs/testing/evidence/f7/21-05-base-runs.log 2>&1       # k6 na configuração BASE + docker stats
# P1-1: exige o override OTLP TEMPORÁRIO (não é a configuração base):
docker compose -f infra/compose/docker-compose.yml -f docs/testing/evidence/f7/21-04-otel-override.yml --env-file infra/compose/.env up -d otelcol gateway
bash docs/testing/evidence/f7/21-04-p1-1.sh c > docs/testing/evidence/f7/21-04-c.log 2>&1
bash docs/testing/evidence/f7/21-04-all.sh        # a1, a2, b1, b2 (um log por caso)
bash docs/testing/evidence/f7/21-04-repeat.sh     # b1-r2, b1-r3
# restaurar a configuração base: remover o contêiner otelcol e recriar o gateway só com o arquivo base
```

Observações sobre reprodução: os scripts 07 e 08 usam chaves de idempotência com sufixo por execução e datas fixas de calendário; reexecutá-los acumula linhas na base de desenvolvimento e `08` usa a data 2026-03-10 (a asserção de projeção 12,34 / 1 supõe data limpa; use `docker compose ... down -v` na base de DEV se precisar de estado virgem). Registre uma nova execução sob outro nome de log em vez de sobrescrever o canônico.

## Inventário da suíte .NET (mapeado por nome)

| Projeto | Testes | Classes (contagem de métodos quando conhecida) |
|---|---|---|
| `tests/CashFlow.Persistence.Tests` | 49 | ConnectionBudgetTests (6), ConsolidationGrantsTests (3), MigrationTests (8), ReconRoGrantsTests (1), RoleAndHealthTests (9) e demais |
| `tests/CashFlow.Transactions.Api.Tests` | 169 | IdempotencyTests (15), RegistrationTests (18), DomainTests (15), AuthorizationTests (10), AuthenticationModeTests (5), DatabaseContractTests (12), DualListenerTests (6), OutboxFactory/RequestHasher/OutboxMessageMapping/PostgresErrorLogging/TracingFilter |
| `tests/CashFlow.Consolidation.Tests` | 66 | ConsolidationWorkerTests (12), ReconToolTests (17), OutboxPublisherTests (3), EndToEndRecoveryTests (2), TopologyTests (9), DeliveryLimitCharacterizationTests (1), ConsolidationApiTests (6) |
| `tests/CashFlow.Security.Tests` | 68 | GatewayEdgeTests (8), HeaderAllowlistTests (2), IdpOutageTests (1), MutualTlsTests (6), SmokeTests (3), TenantIsolationTests (5), TokenClockAndKeyRotationTests (5), TokenValidationTests (11), UnitTests (10) |

Contagens por projeto do Passo B (`21-02-suite.log`). As contagens por classe nas linhas acima são da rodada anterior (339 testes) e NÃO foram recalculadas; a diferença (+12 em Transactions.Api, +1 em Security) vem das correções PF-03 e P1-1.

Atenção: nomes `T09`/`T10` dentro da suíte NÃO coincidem com T-09/T-10 do playbook (por exemplo `T09_TwoConcurrentWorkers_*` trata de dois workers concorrentes). O mapeamento acima é conservador.

## Mudanças em `tests/` feitas por F7

- `tests/CashFlow.Persistence.Tests/PostgresFixture.cs` e `tests/CashFlow.Consolidation.Tests/Support/RabbitMqFixture.cs`: `WithCreateParameterModifier` fixa `HostIP=127.0.0.1` nas publicações (GAP-F5-10). Antes: postgres e rabbitmq em 0.0.0.0 e [::]; depois: somente 127.0.0.1 (`f7/13-fixtures-ports-ANTES.log` x `f7/13-fixtures-ports-DEPOIS.log`). Residual: o contêiner reaper ryuk do Testcontainers continua em 0.0.0.0 (PF-05).
- O fixture do Keycloak (`tests/CashFlow.Security.Tests/Support/KeycloakFixture.cs`) NÃO foi alterado por F7; foi observado em 127.0.0.1 em `13-fixtures-ports-DEPOIS.log`, mas o log ANTES não incluiu a suíte de segurança, então não há comparação antes/depois para ele.

## Regras de evidência (mantidas)

- Testes unitários sem DB real não provam consistência de concorrência.
- `50 req/s` informa duração, VUs, throughput real, percentuais 2xx/4xx/5xx/timeouts e latência (tabela acima).
- Não usar apenas HTTP 200: o k6 compara cada corpo ao oráculo SQL.
- Se Testcontainers ou o Docker estiverem indisponíveis, declarar o impedimento e não marcar como validado.
