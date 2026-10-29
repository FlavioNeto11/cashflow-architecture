# ADR-0001 — Topologia de runtime: lançamentos e consolidação

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1 (revisado em F1). Responde a DEC-03.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- RNF-01 (EX, A-08): o registro de lançamentos não pode ficar indisponível se o consolidado parar. A cláusula de desclassificação torna este o requisito de maior peso.
- RNF-02/03 (EX): o consolidado recebe picos de 50 RPS com no máximo 5% de perda de requisições HTTP.
- Orientação B do avaliador: desacoplamento entre os dois serviços; decisão e justificativa pesam mais do que implementação.
- Restrição de engenharia do projeto: evitar overengineering; comparar monólito modular e dois serviços antes de escolher.
- Volume de lançamentos e prazo não informados (hipóteses em `cost-estimate.md`).

## Alternativas consideradas

Pontuação completa em `docs/architecture/decision-matrix.csv`, horizonte **E1** (alvo pequeno): A = 125, B = 130, C = 140, C sem broker = 125, de 175; B repontuado no horizonte protótipo (E0) = 142. Sem o critério `aderencia_orientacoes_avaliador_B`: A 113, B 114, C 120 (de 155).

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **A** Monólito modular: um host HTTP com os módulos Lançamentos e Consolidação + worker em processo separado lendo a outbox por polling (**sem broker**) + um banco com schemas por módulo | Menor operação e custo; transação local única possível; menos canais a proteger; mais rápido no prazo | Consulta do consolidado (50 RPS) compartilha processo, thread pool e pool de conexões com o `POST`: pico ou defeito na consulta degrada o registro; T-04 só demonstrável para o worker, não para a consulta; exige explicar à banca por que ignorou a orientação de desacoplamento |
| **B** Dois serviços + worker, dois servidores de banco, Outbox + broker + Inbox | Isolamento completo do consolidado, inclusive do store; escala autônoma da leitura e do seu banco | Dois servidores de banco para operar (backup, HA, credenciais, monitoramento) sem requisito de volume que justifique; mais canais, certificados e segredos; maior esforço no prazo |
| **C** Dois deployables HTTP + worker, **um** servidor PostgreSQL com **dois bancos** e roles distintas, Outbox + broker + Inbox | Mesma fronteira lógica de B (dois bancos impedem `JOIN` cross-context); todos os componentes do consolidado param separadamente (T-04/T-05 completos); migrar para B é trocar connection string; um servidor de banco a operar | Servidor PostgreSQL compartilhado é dependência comum (sua queda derruba o `POST`, como em qualquer opção, pois `transactions_db` é a fonte); mais peças do que A (broker, segundo host HTTP) |

Variantes avaliadas e descartadas: (i) schemas em vez de bancos — mais fraco, permite `JOIN` acidental e não força separação de credencial; (ii) sem broker, worker lendo a outbox por polling (`C_sem_broker`, 125) — simplificação válida (ver sensibilidade), mas acopla o consolidado ao schema da fonte, adiciona polling sobre o banco da fonte e elimina a prova das falhas "broker parado" e "payload inválido → DLQ" que o projeto quer demonstrar (`docs/testing/test-plan.md`, T-05 e T-13; não são exigência da banca); (iii) consumidor hospedado dentro de `consolidation-api` — aceitável se o prazo apertar; separado por padrão para que o número de consumidores não acompanhe as réplicas da consulta e para que T-10 use `docker kill`.

## Decisão recomendada

Recomendar **C** para o protótipo e como base do alvo:

- `transactions-api` (inclui Outbox publisher como `BackgroundService`), `consolidation-api`, `consolidation-worker`, `recon-tool` sob demanda.
- Um servidor PostgreSQL, bancos `transactions_db` e `consolidation_db`, seis papéis (`tx_app`, `cons_ro`, `cons_worker`, `recon_ro`, `recon_writer`, `migrator`) com `CONNECTION LIMIT` e timeouts por papel; modo de degradação do servidor compartilhado declarado em `target.md` 7.2a (T-04c, T-08 com `POST`).
- RabbitMQ com fila durável e DLQ (ADR-0002).
- Separação de servidores de banco (B) permanece como ALVO opcional por escala/isolamento.

**Condições:** aprovação de DEC-03 pelo responsável; declaração explícita do servidor de banco compartilhado em toda a documentação e na defesa; privilégio mínimo por role.

## Consequências e trade-offs

- Positivas: RNF-01 demonstrável por `docker stop` de cada componente exclusivo do consolidado; leitura escalável de forma independente; fronteira de dados preservada; caminho de evolução declarado.
- Negativas: consistência eventual visível ao usuário (`asOf`, lag); três processos de aplicação e um broker a operar; dois conjuntos de migrações.
- **Sensibilidade** (`target.md` 2.3, pesos na escala 2–5): A só vence no cenário S2 (prazo pela metade **e** banca sem exigência de demonstração de falhas **e** baixa carga); nesse caso mudar este ADR para A mantendo Outbox/Inbox e worker separado e removendo o broker. B não supera C por pesos com as pontuações E1; supera por 2 pontos no horizonte E0 (Compose) e quando o servidor compartilhado vira gargalo medido. Com volume alto real, evoluir para B.
- Banco candidato: PostgreSQL (`numeric` exato, `ON CONFLICT`, `SKIP LOCKED`, índices parciais, `psql` já disponível no ambiente). Alternativas: SQL Server (licença e imagem pesada), MySQL (sem `SKIP LOCKED` em versões antigas e `ON DUPLICATE KEY` menos expressivo). Não há requisito que imponha um SGBD.

## Validação e evidências previstas

- T-04: parar `consolidation-api` e `consolidation-worker`; medir taxa de sucesso de `POST` antes/durante/depois; verificar aparição dos lançamentos no consolidado após retorno.
- T-05: parar `rabbitmq`; `POST` continua `201`; outbox pendente cresce; após retorno, backlog publicado e aplicado.
- T-08: 50 RPS no `GET` do consolidado com `POST` concorrente em baixa taxa, para evidenciar não interferência.
- Evidências a registrar em `docs/testing/evidence-log.md` quando executadas. Nenhuma existe hoje.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
