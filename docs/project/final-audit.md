# Auditoria final (F8)

Data: 2026-10-29. **G2 aceito pelo responsável em 2026-10-29** (registro em `docs/project/approvals.md`). Este documento é a auditoria que embasou o aceite, não o aceite em si. A seção 4 também registra o estado de cada lacuna depois de F9 (correções e reexecuções feitas após o aceite, com logs em `docs/testing/evidence/f9/`).

## 1. Origem e método

- **Auditoria de qualidade:** revisão somente leitura de requisitos, rastreabilidade, documentação e evidências, sem executar build, Docker ou k6 e sem ler `infra/compose/.env`.
- **Revisão do ponto de vista da banca:** leitura crítica da implementação, dos ADRs e dos logs, para preparar as perguntas da defesa.
- **Revisão de arquitetura:** somente leitura, sobre o README, `implemented.md`, este documento, `acceptance.md` e os logs; achados na seção 2.1.
- **Conferência e correções:** cada achado foi conferido no repositório; as correções de documentação autorizadas em C-36 foram aplicadas, e abaixo está o que foi corrigido em F8 e o que segue aberto.
- **Severidade:** P0 = afirmação falsa voltada ao avaliador ou entregável obrigatório ausente; P1 = contradição interna que pode enganar na defesa ou esconder limitação conhecida; P2 = questão histórica ou cosmética.

Os números citados vêm dos logs em `docs/testing/evidence/` e de `docs/testing/evidence-log.md`. Nada aqui vale para produção: todas as medições são de um host local com Docker Desktop.

## 2. Achados da auditoria e tratamento em F8

| # | Sev. | Achado | Tratamento em F8 |
|---|---|---|---|
| 1 | P0 | `README.md` da raiz era o inicial, só de documentação ("nenhum serviço .NET implementado"), sem quickstart | Reescrito. Quickstart executado do zero (`down -v`): a 1ª tentativa falhou com 401 porque o token era pedido antes de o one-shot `keycloak-init` terminar (`docs/testing/evidence/f8/01a-readme-quickstart-tentativa1.log`); com `up --wait` e `docker wait cashflow-keycloak-init-1` no README, a 2ª passou (`01b-readme-quickstart-tentativa2.log`); depois das correções da revisão de arquitetura (seção 2.1), a 3ª, com o README final, também passou: POST 201, consolidado 200 com `credits` 100.00, oráculo SQL 1 linha, nenhum JWT no log (`docs/testing/evidence/f8/01-readme-quickstart.log`). `.env` e certificados já existiam |
| 2 | P1 | "Fatos de ambiente" do registro de andamento dizia .NET 10 não instalado e DEC-10 pendente | Corrigido (SDK 10.0.403, G1-08; k6 em container) |
| 3 | P1 | `decisions-needed.md`, `approvals.md` e `docs/adr/README.md` pareciam sem aprovação | Notas F8: DEC-01..10 resolvidas por G1; ADRs 0001–0011 ACEITOS em G1; aceite ≠ validação |
| 4 | P1 | `traceability.csv` todo PROPOSTO; `RF.md`/`RNF.md` diziam "nada implementado"; `risk-register.csv` desatualizado | Status, T-ids e evidência por requisito em `traceability.csv`; notas F8 em RF/RNF; R-21..R-23 atualizados |
| 5 | P1 | `security.md`: scan "não executado", rótulos T-NN trocados, `merchant_id` "não testado", título "F7 pendentes" | Corrigido (trivy de F7, T-26/T-25/T-32, T-31 PARCIAL) |
| 6 | P1 | Não há CI de build e teste | Aberto em F8; **corrigido em F9** (`.github/workflows/ci.yml`; seção 4, item 7) |
| 7 | P1 | C-22, C-F5-09, C-34 e C-35 sem ciência registrada do responsável | Aberto em F8; aceitos pelo responsável no G2 (seção 4, item 18) |
| 8 | P1 | `infra/README.md`: estado "pós-F5", redrive "não demonstrado", senhas "literais do exemplo" | Corrigido (rebuild de F7, redrive de F4/F7, rotação C-30) |
| 9 | P2 | Linha F7 do registro de andamento sem dizer que a rodada pós-rebuild usou 1 token | Corrigido |
| 10 | P2 | Linha F1 do registro de andamento ("src/ contém só README") histórica | Mantida como histórico; o estado atual está em `implemented.md` |
| 11 | P2 | recon-tool imprime `Cannot load library libgssapi_krb5.so.2` (`evidence/f4/verify-compose-scenarios-round4.log:237`) | Aberto (ruído do Npgsql na imagem; sem efeito no resultado) |
| 12 | P2 | Varredura de segredos (14:19Z) anterior à rotação de 15:15Z | Reexecutada às 16:13Z (`docs/testing/evidence/f8/02-secret-scan.log`): 595 arquivos, 13 variáveis do `.env`; 10 com o valor do exemplo, só no `.env.example`; as 3 rotacionadas sem ocorrência; 0 arquivos com JWT; saída 0 |

Achados extras da conferência em F8:
- `docs/architecture/implemented.md:3` dizia "NO MOMENTO: NENHUMA IMPLEMENTAÇÃO" (apontado na revisão do ponto de vista da banca); reescrito com processos, endpoints, controles por canal e a tabela alvo × implementado × futuro.
- `target.md`, `ADR-0002` ("nenhuma está aprovada") e o contexto do `ADR-0008` ("não instalado") estavam desatualizados; receberam nota pós-G1.
- A linha C-34 de `decisions-needed.md` continha caracteres de controle literais (BEL, US e CR) no lugar de `\x07`/`\x1f`, o que quebrava a linha da tabela; substituídos pela forma escapada.
- Falso positivo descartado: a revisão do ponto de vista da banca apontou divergência entre o ADR-0011 e o código sobre `RequireHttpsMetadata` e entre documentos sobre algoritmos JWT. Conferido: o padrão é RS256/ES256 com lista permitida RS*/PS*/ES*, e os textos foram alinhados nesses termos; não havia divergência de comportamento.

### 2.1 Revisão de arquitetura em F8

Feita depois das correções da auditoria. Conferidos como corretos: 352 testes; 6001 requisições, 0,00%, p95 6,88 ms e p99 9,93 ms; 22,79% e 5,05% de 429; 30001 requisições; Trivy 91 = 88 + 3; rotas, variáveis e portas do README; SDK 10.0.403.

| # | Sev. | Achado | Tratamento em F8 |
|---|---|---|---|
| R1 | P0 | O README dizia 409 para a mesma chave com outro corpo; o código devolve 422 (`TransactionEndpoints.cs:186`) | Corrigido: 422 para outro corpo, 409 para chave ainda em processamento (ADR-0005) |
| R2 | P1 | A demo do README usava a data UTC; a data de negócio é a de `America/Sao_Paulo` e data futura é rejeitada | Corrigido (`date -u -d '-3 hours'`); quickstart reexecutado (3ª execução) |
| R3 | P1 | Não havia varredura de segredos depois da rotação de 15:15Z, embora o texto a desse por feita | Executada: `docs/testing/evidence/f8/02-secret-scan.log` (achado 12 da seção 2) |
| R4 | P1 | `target.md`, ADR-0002 e ADR-0007 ainda diziam "a decidir em G1" | Alinhados a G1-03, G1-10, G1-11, G1-12 e G1-13 |
| R5 | P1 | Logs `03`, `07` e `08` anteriores ao rebuild de 14:33Z marcados VALIDADO sem ressalva | Ressalva em `implemented.md`, `acceptance.md`, `traceability.csv` e `evidence-log.md`; item 8 da seção 4 ampliado |
| R6 | P2 | "76 pendentes zerados em 13,6 s" misturava uma amostra com a referência de tempo | Reescrito: amostra de 76 durante a queda; outbox zerada 13,6 s após o broker ficar healthy (27,3 s após o restart) |
| R7 | P2 | Lock do recon citado como 36–62 ms, sem o caso com correção | 36–62 ms sem divergência (F7); 111–118 ms com correção ou rebuild (F4, `round4.log`) |
| R8 | P2 | PF-18 citado como 0,10–0,90% | 0,10%, 0,00%, 0,00% e 0,90% em 4 rodadas: intermitente |
| R9 | P2 | As rodadas `21-05` apareciam como 2 tenants; usaram 1 token (`TOKENS=A`) | Corrigido; 2 tokens só em `06-t08-get-two-tokens.log`, anterior ao rebuild |
| R10 | P2 | Negativos de mTLS "só na suíte" sem citar o teste; cliente público com PKCE do realm omitido | Citado `MutualTlsTests.cs`; `cashflow-web` (PKCE S256) listado como IMPLEMENTADO, NÃO VALIDADO |
| R11 | P2 | A "saida 0" do script do quickstart não provava sucesso: a 1ª tentativa saiu 0 com 401 | O trecho do README usa `set -euo pipefail` e `--fail-with-body`; o script também confere HTTP e o oráculo SQL |
| R12 | P2 | Porta de health 8081, em HTTP, alcançável por toda a rede `app` | Sem mudança: já declarado em `security.md`; o isolamento por porta funciona (negócio na 8081 → 404) |
| R13 | P2 | O aceite do README não dizia que `.env` e PKI já existiam | Texto: "a partir de volumes vazios, com `.env` e PKI preexistentes" |

## 3. Mapa de aceite

O mapa detalhado, com evidência por item, está em `docs/requirements/acceptance.md`. Resumo:

- **G1:** todos os itens marcados, com referência a `approvals.md` e aos itens G1-NN.
- **Checkpoints internos (integridade):** marcados com evidência de F4 e F7.
- **Checkpoint de qualidade:** k6 e cenários de falha marcados com ressalvas; logs/métricas/trace PARCIAL; CI marcado em F9.
- **Entrega:** README, alvo × implementado × futuro e lista de lacunas marcados depois do quickstart; publicação no GitHub marcada; ensaio da defesa desmarcado (depende do responsável).
- **Aceite final G2:** marcado; aceito pelo responsável em 2026-10-29.

## 4. Lacunas e riscos para o G2, e estado após F9

A coluna "Severidade em F8" é a da auditoria que embasou o G2. A coluna "Estado após F9" registra o que foi corrigido ou reexecutado depois do aceite, com o log correspondente, e o que segue aberto, com o motivo.

| # | Item | Severidade em F8 | Estado após F9 | Evidência ou caminho |
|---|---|---|---|---|
| 1 | README sem quickstart | P0 → corrigido em F8 | Sem mudança: validado só em Windows/Git Bash, com `.env` e PKI preexistentes | `README.md`, `evidence/f8/01-readme-quickstart.log` |
| 2 | PF-18: rajada de um merchant gerava 429 para outro, de forma intermitente (0,10%, 0,00%, 0,00% e 0,90% em 4 rodadas) | ALTO | **Corrigido.** Limitadores encadeados: o limite do merchant é avaliado antes do teto global, e a recusa por merchant não consome o teto. Teste determinístico falhava antes e passa depois; na stack, merchant B com 0 respostas 429 em 2 rodadas (1001 e 1000 GETs) enquanto o merchant A recebia 83 e 371 respostas 429 nas rajadas. Duas rodadas limpas não provam ausência de intermitência; o teste da suíte é a prova determinística | `evidence/f9/02-pf18-antes.log`, `04-pf18-depois.log`, `20-pf18-stack-b1.log`, `20-pf18-stack-b2.log` |
| 3 | PF-17: byte UTF-8 inválido no corpo devolvia 500 | MÉDIO | **Corrigido.** Agora 400; testes falhavam antes e passam depois; na stack, o mesmo pedido do roteiro `21-07` recebe 400 (o roteiro, escrito em F7, ainda espera 500 e por isso marca 1 FALHA: é a prova da correção, não regressão) | `evidence/f9/01-pf17-antes.log`, `03-pf17-depois.log`, `19-rerun-21-07-pf17-stack.log` |
| 4 | T-08 e T-30 com 1–2 tenants em vez dos 10 do ADR-0009 | ALTO | **Reexecutado com 10 tenants.** 6001 GETs a 50,01/s, 0,00% de perda, p95 7,09 ms, p99 10,82 ms, 600–601 respostas corretas por merchant contra o oráculo SQL de cada um. 8 dos 10 clients eram temporários (removidos ao fim); os merchants A e B não tinham movimento na data, os outros 8 tinham 3 lançamentos cada | `evidence/f9/14-t08-10tenants.log`, `14-stats.txt`, `k6/t08-10tenants.json` |
| 5 | Rate limit em memória por instância (os limites se multiplicam ao escalar) | ALTO no alvo | **Não fechável no MVP**: exige um armazenamento compartilhado (por exemplo Redis) ou o limitador do gateway gerenciado; é decisão do alvo, não um defeito de uma instância | `docs/architecture/security.md` |
| 6 | Host Linux nativo não validado | MÉDIO | **Parcial.** O CI roda build e as 4 suítes num host Linux (`ubuntu-latest`), inclusive Testcontainers. Continuam sem validação em host Linux: a stack Compose e o `make-certs.sh` (exige OpenSSL ≥ 3.4; o runner tem 3.0) | `evidence/f9/21-ci-github-actions.log`, `evidence/f7/19-make-certs-linux.log`, C-25 |
| 7 | Sem CI de build e teste | ALTO | **Corrigido.** GitHub Actions: build Release com avisos como erro e as 4 suítes, 364 testes aprovados. A stack e o k6 não rodam no CI | `.github/workflows/ci.yml`, `evidence/f9/21-ci-github-actions.log` |
| 8 | `02`, `03`, `07` e `08` e o SIGKILL (PF-14) não reexecutados com as imagens atuais | MÉDIO | **Reexecutados.** `02`: 87 OK, 0 FALHA; `03`: 0 FALHA; `07` (R0–R5, com SIGKILL do worker, da transactions-api e do gateway): FALHAS=0, nenhuma perda nem duplicata; `08` (D1–D5): FALHAS=0 | `evidence/f9/15b-rerun-02-security.log`, `16-rerun-03-idempotency.log`, `17-rerun-07-resilience.log`, `18-rerun-08-dlq.log` |
| 9 | PF-01 estrutural (worker serial, lock do recon) | P2 | **Não fechável sem mudar o desenho**: paralelizar o worker exige particionar a fila por merchant; o lock do recon é intencional. Fica como limite declarado | C-29 |
| 10 | ryuk do Testcontainers exposto: P2 nesta máquina, P1 num runner de CI | P2/P1 | **Declarado.** No CI o runner é efêmero e exclusivo da execução, o que reduz o risco; não há mitigação no código | C-31 |
| 11 | Trivy: 91 HIGH+CRITICAL em imagens de terceiros; `cashflow/*` com 0 antes do rebuild | MÉDIO | **Reexecutado com as imagens atuais; declarado.** As 5 imagens `cashflow/*` seguem com 0 HIGH/CRITICAL (26 MEDIUM/LOW, da base `ubuntu 24.04`). Terceiros: `postgres:17` com 88 (86 HIGH e 2 CRITICAL), `quay.io/keycloak/keycloak:26.8.1` com 3, `rabbitmq:4-management` com 0; total 91, igual a F7. Os digests fixados no Compose não foram trocados: a atualização das imagens de terceiros fica para o alvo, com reavaliação a cada nova tag | `evidence/f7/12-trivy-imagens.log`, `evidence/f9/23-trivy-imagens.log` |
| 12 | Purge da DLQ sem trilha (PF-09); job de retenção do Inbox nunca exercitado (I6) | MÉDIO | **PF-09 corrigido**: `recon-tool dlq purge` grava uma trilha por mensagem antes do ACK (29 testes; na stack, DLQ 3 → 2 → 0 com 3 trilhas). **I6 segue aberto**, e a descrição de F8 estava imprecisa: não existe job de expurgo, só os limiares de `InboxRetention.cs` e a guarda de 29 dias. Criar o job exige um papel com `DELETE` separado dos papéis de aplicação (C-15); fica para o alvo | `evidence/f9/05-pf09-purge.log`, `18-rerun-08-dlq.log` D5; C-15 |
| 13 | NÃO COBERTOS: T-04c, T-05b, T-11, T-12, T-14b, T-16, T-19 (saturação), T-22, T-29, borda de fuso UTC, `57014`, `connection.blocked` | MÉDIO | **Sem mudança.** Cada um exige cenário próprio; ficam declarados | `docs/testing/test-plan.md` |
| 14 | Sem métricas de lag, idade da DLQ e da outbox; rotação de certificado e de IdP não testadas; PKCE não demonstrado | MÉDIO | **Sem mudança.** Existem e são verificadas em teste `inbox_duplicates_total` e `consumer_paused_seconds`; `dlq_oldest_age_seconds`, `outbox_*` e `reconciliation_divergences_total` continuam só no desenho | `docs/architecture/deployment.md` seção 5, `security.md` |
| 15 | Token sem a claim `merchant_id` não reexecutado com as imagens atuais | MÉDIO | **Reexecutado.** Client temporário sem o mapper: POST e GETs recusados com 403, nenhum lançamento gravado, client removido | `evidence/f9/13-token-sem-merchant.log` |
| 16 | Keycloak em `start-dev`; `.env.example` com valores de desenvolvimento | BAIXO (dev, loopback) | **Declarado**: é o perfil de desenvolvimento local; o alvo usa IdP gerenciado ou Keycloak em modo produção | `evidence/f7/17-secret-scan.log` |
| 17 | Três credenciais passaram pela saída do terminal e foram rotacionadas | Declarar | **Declarado**, sem mudança | C-30, `evidence/f7/23-credential-rotation.log` |
| 18 | C-22, C-F5-09, C-34 e C-35 sem ciência do responsável | Depende do responsável | **Aceitos no G2.** C-F5-09 passou de fail-open a fail-closed em F9; PF-17 e PF-18 (C-35) corrigidos em F9; C-22 (usuário AMQP único) e C-34 seguem como desvios aceitos | `docs/project/decisions-needed.md`, `approvals.md` |
| 19 | Ensaiar a defesa | Depende do responsável | **Depende do responsável** | — |
| 20 | CPU alta em rodada só de GET; não investigado | BAIXO | **Reamostrado, não investigado.** Na rodada de 10 tenants, picos de 167,77% no RabbitMQ, 106,96% no Keycloak, 104,57% na consolidation-api e 61,02% no gateway, com médias entre 1,6% e 20,0%; a causa dos picos não foi analisada | `evidence/f9/14-t08-10tenants.log` seção 5, `14-stats.txt` |

Caminhos `evidence/...` são relativos a `docs/testing/`.

## 5. Escopo

A auditoria não viu overengineering grave: três processos, um PostgreSQL e um broker, como aprovado em G1. O peso de gateway, IdP e mTLS vem das orientações do avaliador. As lacunas são de cobertura e de operação, não de desenho.

## 6. Conclusão

Fechar as fases F0–F7 significa que o trabalho planejado foi feito, não que o produto está certificado. Com o P0 corrigido e o quickstart executado, a entrega técnica estava pronta para a revisão do responsável, que a aceitou no G2. Depois do aceite, F9 corrigiu PF-17, PF-18, PF-09 e C-F5-09, criou o CI e reexecutou com as imagens atuais os roteiros que só tinham log anterior ao rebuild, além do perfil de 10 tenants. Seguem abertos, e devem ser apresentados na defesa, os itens 5, 6 (parcial), 9, 10, 12 (I6), 13, 14, 16, 17, 19 e 20 da seção 4.
