# Runbook: IdP (Keycloak) indisponível

**Estado.** Comportamento descrito a partir de F5 (`CashFlow.Security.Tests`, `IdpOutageTests`, T-07) e do desenho; **[NÃO EXECUTADO]** no Compose nesta fase (o Keycloak não foi parado em F6).

| Situação | Comportamento esperado |
|---|---|
| Processo (gateway/API) **aquecido** | Segue validando tokens com o JWKS em cache (refresh de 60 min). Tokens novos não podem ser emitidos. |
| Processo que sobe **a frio** sem o IdP | `/health/ready` = 503 (check `oidc-metadata`); tokens válidos recebem `401 metadata_unavailable`. Recupera sozinho quando o IdP volta. |

**Ações**
1. Não reinicie gateway/APIs durante a queda: um processo reiniciado perde o cache e fica 503.
2. Verifique o IdP: `docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env ps keycloak` e `logs --tail 50 keycloak`.
3. Suba-o: `up -d keycloak`. Ele leva ~30 s a ficar `healthy` (volume de realm importado na primeira criação; `keycloak-init` reaplica secrets com `docker compose run --rm keycloak-init`).
4. Confirme `/health/ready` de gateway e APIs voltando a 200 (`docker compose ps`).

**Impacto no negócio.** Leituras/escritas com token já emitido seguem enquanto os processos estiverem aquecidos e o token não expirar (`exp` de 300 s); depois disso, 401. Os lançamentos já confirmados não são afetados.
