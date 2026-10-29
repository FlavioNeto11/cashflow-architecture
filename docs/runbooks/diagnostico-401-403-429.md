# Runbook: diagnóstico de 401, 403 e 429 (gateway e APIs)

**Estado.** Os nomes abaixo vêm do código de F5 (`SecurityMetrics.cs`, `CashFlow.Hosting.Security`); as métricas **não foram vistas no coletor** em F6 (`deployment.md` seção 5.1) e o roteiro de F5 mostra os códigos HTTP (`29b-gateway-scenarios-rerun.log`, seções 4 e 5). Não há alerta configurado.

- **Logs (sem token):** categorias `CashFlow.Hosting.Security` e `CashFlow.Hosting.Tls`. `docker compose ... logs --tail 100 gateway transactions-api | grep -i security`.
- **Métricas** (meter `CashFlow.Security`): `authn_failed_total{reason}` (401), `authz_denied_total{kind}` (403), `rate_limited_total{route}` (429), `mtls_handshake_failed_total{side,reason}`.
- **401:** token ausente/expirado/emissor errado; `401 metadata_unavailable` = IdP fora (ver `idp-indisponivel.md`). Confira o `iss` esperado `http://keycloak:8080/realms/cashflow`.
- **403:** falta de escopo (`transactions.write`, `consolidation.read` etc.) ou merchant diferente do token; 404 idêntico para recurso de outro merchant (ADR-0006).
- **429:** limite por merchant (padrão 60/s) ou global (120/s por instância); números são HIPÓTESE até T-08 (F7). `Retry-After` indica a janela.
- **502 no gateway:** API ainda subindo, certificado do gateway bloqueado (ver `certificados-e-revogacao.md`) ou API caída.
- Cliente Windows: `curl --ssl-revoke-best-effort --cacert infra/pki/out/ca.crt` (ver `certificados-e-revogacao.md`, seção 4).
