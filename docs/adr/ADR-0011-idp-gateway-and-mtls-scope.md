# ADR-0011 — IdP local, API Gateway e escopo de mTLS no protótipo

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a DEC-06, DEC-07 e RNF-04. Complementa ADR-0003.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

- Orientação B: OAuth2/JWT, API Gateway e mTLS reais, sem simplificar.
- Nenhum recurso pago; tudo deve rodar em containers locais.
- Diretriz do projeto: TLS comum não é mTLS; testes devem rejeitar certificado de cliente ausente/expirado/não confiável.
- Prazo não informado: escolher o conjunto mínimo que **demonstra** cada controle de verdade e declarar o resto como gap.

## Alternativas consideradas

### IdP (DEC-06)

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **Keycloak** em container (candidato) | OIDC completo (Authorization Code + PKCE, Client Credentials), JWKS, mappers de claim (`merchant_id`), realm importável, amplamente conhecido pela banca | Imagem pesada; subida lenta; modo dev usa HTTP; realm JSON não pode conter segredos reais |
| Duende IdentityServer | Mesmo stack .NET | Licença comercial fora de dev/teste; mais código próprio |
| Mock OIDC server | Leve | Não demonstra IdP real; aceitável só como fallback rotulado |
| IdP gerenciado em nuvem | Produtivo | Recurso externo/pago; não permitido sem autorização |

### Gateway

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **YARP** (candidato) | Mesmo stack .NET; JWT, rate limiting (`Microsoft.AspNetCore.RateLimiting`), limites de corpo, headers e certificado de cliente para upstream com configuração conhecida pelo time | Não é WAF; sem UI de gestão |
| Envoy / NGINX / Kong | Maduros | Linguagem de configuração própria; mTLS e JWT exigem módulos/plugins; mais superfície para o prazo |
| Gateway gerenciado do provedor | Produtivo | Não executável localmente |

### Escopo de mTLS (DEC-07)

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **(i)** mTLS **a implementar no protótipo** em Gateway→`transactions-api` e Gateway→`consolidation-api`; demais canais TLS/privados declarados | Demonstra o canal que a banca vai perguntar; CA local e scripts de certificado simples | Banco, broker, IdP e telemetria sem TLS no protótipo (gaps declarados) |
| **(ii)** mTLS em todos os canais | Completo | Configurar TLS em PostgreSQL, RabbitMQ, Keycloak e OTLP com certificados de cliente consome prazo sem acrescentar prova além de (i) |
| **(iii)** Tudo apenas projetado | Rápido | Fraco na defesa; não há rejeição comprovada de certificado |

## Decisão recomendada

- **IdP:** Keycloak em container, realm `cashflow` importado de arquivo versionado **sem segredos reais** (segredos de clientes de desenvolvimento gerados/injetados por variável de ambiente na subida), dois merchants de demonstração, clientes `cashflow-web` (público, Authorization Code + PKCE) e `merchant-a-system`/`merchant-b-system` (Client Credentials), mapper `merchant_id` (atributo de usuário **editável só por admin** no user profile do realm), **audience mapper** `cashflow-api`, escopos `transactions.write`, `transactions.read`, `consolidation.read`. **Issuer único** (G1-14): `KC_HOSTNAME=http://keycloak:8080` fixo, de modo que host e containers recebem o mesmo `iss`; `ValidateIssuer = false` proibido; `RequireHttpsMetadata = false` só em `Development`; alternativa `localhost:8080` com `ValidIssuer` explícito e `MetadataAddress` interno (`security.md` 3.3). Keycloak local é simulador OIDC real, **não** IAM produtivo.
- **Gateway:** YARP com TLS de borda (certificado de desenvolvimento), validação JWT, rate limiting (números em `security.md`), limite de corpo, CORS fechado, headers de segurança, correlação, encaminhamento com **certificado de cliente** para as APIs. Gateway **não** autoriza por merchant.
- **mTLS (i), validado nos dois lados:** APIs com Kestrel exigindo certificado de cliente emitido pela CA local e validado (cadeia contra a CA local, validade, SAN `gateway.internal`, EKU `clientAuth`, blocklist de thumbprint); **YARP apresenta o certificado de cliente e valida o certificado de servidor das APIs** contra a mesma CA com verificação de hostname — `DangerousAcceptAnyServerCertificateValidator` proibido. Scripts geram CA, certificados de servidor e de cliente e os certificados negativos de teste (expirado, SAN errado, EKU errado, segunda CA) em diretório ignorado pelo Git, validade 90 dias (HIPÓTESE), rotação = regenerar e reiniciar. Revogação no MVP = blocklist de thumbprints (GAP parcial; CRL/OCSP no ALVO). `/health/*` em listener separado, fora da porta mTLS e fora do gateway.
- **Gaps declarados do protótipo:** serviços→PostgreSQL, publisher/worker→RabbitMQ, gateway/APIs→Keycloak (JWKS) e serviços→telemetria em rede privada do Compose **sem TLS**. ALVO: TLS obrigatório (`sslmode=verify-full`, AMQPS, HTTPS, OTLP com TLS), credenciais por serviço, mTLS/cert-auth onde o provedor suportar.
- **IdP indisponível:** gateway e APIs validam com **JWKS em cache** (refresh periódico e sob `kid` desconhecido com limite de frequência); nenhuma chamada síncrona ao IdP por requisição; tokens válidos continuam aceitos **em processo já aquecido**; novos logins/emissões falham; sem introspecção remota. **Cold start com IdP fora (G1-15):** recomendação = declarar GAP (readiness falsa até obter JWKS; `gateway` sem `depends_on: service_healthy` do Keycloak); alternativa = persistir o último JWKS válido em volume com idade máxima (HIPÓTESE 24 h), ao custo de desconhecer chave rotacionada durante a queda.

## Consequências e trade-offs

- Subida local mais lenta (Keycloak); compensada por T-07 real.
- Dois certificados de servidor + um de cliente + uma CA local a gerar e documentar; nunca versionados.
- A matriz canal × protótipo × alvo em `security.md` é o documento de verdade sobre o que é mTLS, TLS ou privado sem TLS.

## Validação e evidências previstas

- T-07: tokens ausente/adulterado/expirado/sem escopo/outro merchant; `429` acima do limite; variante proposta: usuário tenta editar o próprio atributo `merchant_id` → negado.
- **T-17 (proposto)** mTLS: handshake rejeitado sem certificado de cliente, com CA não confiável, **expirado, SAN errado, EKU errado, thumbprint bloqueado**; aceito com o certificado do gateway; gateway recusa upstream com certificado de servidor fora da CA.
- **T-18 (proposto)** IdP: token com `iss` divergente → `401`; parar Keycloak com processos aquecidos → token válido aceito, emissão falha; reiniciar o gateway com Keycloak parado → comportamento conforme G1-15 (readiness falsa ou JWKS persistido).

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
