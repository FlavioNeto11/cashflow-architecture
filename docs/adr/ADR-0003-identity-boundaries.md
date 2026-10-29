# ADR-0003 — Identidade, perímetro e limites de confiança

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1 (revisado em F1). Responde a RNF-04 e à orientação B (segurança corporativa). Detalhes em ADR-0006 (tenant) e ADR-0011 (IdP, Gateway, mTLS).
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- Orientação B: autenticação, autorização, API Gateway, OAuth2/JWT, mTLS e proteção das APIs, sem simplificar por haver poucos serviços.
- RF-05: identidade autorizada para merchant A não lê nem grava dados de B; `merchant_id` do payload não é fonte de autorização.
- Perguntas que esta decisão responde: quem protege o tenant; por que o Gateway não elimina autorização nas APIs; onde exatamente há mTLS.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| Autenticação caseira (usuário/senha próprio, JWT assinado pela aplicação) | Sem dependência externa | Reinventa IAM; sem OIDC/JWKS; fraco na defesa; descartada |
| IdP OIDC real em container local (Keycloak candidato) + validação de JWT no Gateway **e** nas APIs | Demonstra issuer/audience/JWKS/escopos reais; fluxos padrão | Mais um container; realm e clientes a configurar; Keycloak local não é IAM produtivo |
| IdP gerenciado em nuvem | Produtivo | Recurso pago/externo; não permitido no desafio sem autorização |
| Confiar apenas no Gateway (APIs sem validação) | Menos código | Qualquer acesso à rede interna contorna a autorização; viola defesa em profundidade; descartada |
| Token simulado (mock OIDC) rotulado | Rápido | Não demonstra JWKS/issuer/audience; aceitável só como fallback explícito |

## Decisão recomendada

- **Defesa em profundidade em três camadas:** (1) Gateway valida JWT (assinatura, `iss`, `aud`, `exp`/`nbf`) e aplica rate limit, limites de payload e headers; (2) cada API **revalida** o JWT e aplica política de escopo e de merchant (rota = claim); (3) banco com roles distintas por serviço e, como ALVO, Row-Level Security.
- **Limites de confiança:** cliente → gateway (TLS + JWT); gateway → APIs (mTLS + JWT repassado); serviços → banco e broker (rede privada, credenciais por serviço; TLS como ALVO, gap declarado no MVP); APIs/gateway → IdP (JWKS em cache, sem chamada síncrona por requisição).
- **Identidades de workload separadas** de identidades de usuário: gateway (certificado de cliente), publisher e worker (usuários AMQP distintos), APIs e worker (papéis de banco distintos: `tx_app`, `cons_ro`, `cons_worker`), `recon-tool` (`recon_ro` read-only na fonte + `recon_writer` no consolidado), migrações (`migrator`).
- **Segredos** fora do repositório; realm de desenvolvimento sem segredos reais (placeholders gerados na subida).

## Consequências e trade-offs

- Validação dupla do JWT custa microssegundos por requisição e elimina a dependência de "rede interna confiável".
- Keycloak local acrescenta tempo de subida e configuração; em troca, T-07 testa um IdP real.
- mTLS só é afirmado onde o servidor **exige e valida** certificado de cliente (ADR-0011). TLS unilateral nunca é chamado de mTLS.
- Gaps do protótipo (TLS para banco/broker/IdP/telemetria) ficam declarados em `security.md` e no risk register.

## Validação e evidências previstas

- T-07: token ausente, adulterado, expirado, sem escopo, de outro merchant → `401/403/404` conforme ADR-0006.
- Teste de mTLS: chamada direta à API sem certificado de cliente ou com certificado de CA não confiável é rejeitada no handshake.
- Revisão de configuração: algoritmos aceitos, tolerância de relógio, audience, cache de JWKS.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
