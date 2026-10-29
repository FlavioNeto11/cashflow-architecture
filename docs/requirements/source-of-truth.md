# Origem e classificação dos requisitos

> Documentação derivada do enunciado técnico (3 páginas) e de orientações complementares enviadas pelo avaliador. Não é uma reprodução integral dos materiais.

## A. Condições explícitas do desafio

- Comerciante registra créditos e débitos para controlar seu fluxo de caixa; consulta saldo diário consolidado.
- Capacidades/serviços: controle de lançamentos e consolidado diário.
- Entregar mapeamento de domínios funcionais e capacidades de negócio, refinamento RF/RNF, arquitetura-alvo, justificativas de tecnologias/padrões, código funcional, testes, README local, repositório GitHub público e documentação no repositório.
- Lançamentos **não podem** ficar indisponíveis se o consolidado parar.
- Consolidado recebe **50 requisições/segundo em picos**, com **no máximo 5% de perda de requisições**.
- Diferenciais: arquitetura de transição *se necessária*, estimativa de custos, observabilidade e segurança de integrações.
- A ausência dos requisitos técnicos obrigatórios mínimos desclassifica o desafio.

## B. Orientações explícitas posteriores do avaliador

- Decisão arquitetural e justificativa têm mais peso que mera implementação.
- Demonstração de autonomia e conhecimento próprio, sem dependência excessiva de IA.
- Segurança corporativa: autenticação, autorização, API Gateway, OAuth2/JWT, mTLS, proteção das APIs. Não simplificar segurança só porque existem poucos serviços.
- Topologia de infraestrutura e estratégia de deployment.
- Desacoplamento entre os dois serviços.
- RNFs com métricas e metas claras.
- C4, RF/RNF e ADRs explicitando trade-offs.
- Testes autorais orientados aos cenários e riscos do negócio.
- Logs, métricas, observabilidade, tratamento de falhas.
- Priorizar qualidade e domínio, distinguir implementado/arquitetura-alvo/futuro e justificar cada componente.

## C. Hipóteses a validar (não constam como obrigatórias nos materiais)

- Multi-tenant com `merchant_id`: extrapolação de projeto para cenário corporativo; requisito mínimo original fala em **um comerciante**.
- Consulta por período, estorno, retroativos, reconciliação e contrato de idempotência são propostas de engenharia para tratar integridade.
- PostgreSQL, .NET LTS, RabbitMQ, YARP, Keycloak, EF Core, Docker, OpenTelemetry, xUnit e k6 são tecnologias *candidatas*.
- 99% de sucesso interno, p95 300 ms, teste de 10 minutos, disponibilidade 99,9%, RTO/RPO ainda são **metas sugeridas**, não fornecidas.
- Arquitetura em dois serviços é recomendação a confrontar com monólito modular + processos independentes.
- A arquitetura-alvo pode ser mais ampla do que o protótipo; a implementação segura mínima deve ser acordada no gate G1.
- Premissas de domínio propostas na F0 (P-01..P-08 em `RF.md`): decimal escala 2, moeda única BRL, `occurred_at` em UTC, competência derivada no fuso America/Sao_Paulo, crédito/débito com valor positivo, saldo negativo permitido, sem edição física, data sem movimento retorna 0,00. Nenhuma consta do enunciado.

## Ambiguidades abertas

1. 'Perda de requisições' = requisição não atendida/erro/timeout ou perda persistida? Proponha métrica HTTP clara; nunca perder lançamento confirmado.
2. 'Saldo diário consolidado' significa fluxo líquido do dia ou saldo acumulado até a data? Definir ambos ou escolher explicitamente com aprovação.
3. Reversão/edição, data de competência, moeda e fuso horário não estão determinados.
4. Limites de infraestrutura, disponibilidade exigida, escala dos lançamentos e prazo da avaliação não foram informados.
5. Classificação de HTTP 429 (rate limit) no cálculo de perda: proposta F0 é contá-lo como perda; aprovada em G1 (ADR-0009).
6. Se o saldo acumulado (`closing_balance`) for adotado, um lançamento retroativo altera todas as datas posteriores, e o saldo de abertura não é fornecido pelo enunciado.
