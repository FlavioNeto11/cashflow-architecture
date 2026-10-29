# Roadmap condicionado a gates

Fases F0–F8 com gates de aprovação do responsável G1 e G2. Numeração anterior F0–F7 substituída em 2026-10-28.

| Fase | Entregáveis | Critério de saída |
|---|---|---|
| F0 | Análise de lacunas, RF/RNF refinados, ambiguidades, inventário de ambiente | Gaps, RF/RNF e plano explícitos; nenhuma implantação |
| F1 | Comparação de alternativas, C4 L1–L3, sequências de falha, topologia, segurança corporativa, ADRs | C4, segurança, topologia, ADRs e opções comparadas; **G1 pendente de aprovação humana** |
| F2 | Solução .NET, containers locais, migrações, esqueleto testável | Build reproduzível e scaffold funcional local, com evidências |
| F3 | Créditos/débitos, invariantes monetárias, PostgreSQL, API funcional | Precisão financeira e testes registrados |
| F4 | Outbox/Inbox, broker, projeção de consolidação, reconstrução | Falha e recuperação verificáveis sem perda nem dupla contagem |
| F5 | Gateway, OIDC/JWT, autorização por tenant, mTLS protótipo × alvo | Autorização tenant testada; mTLS alvo/protótipo separados |
| F6 | Docker local, topologia de deploy, telemetria básica | Deploy documentado; telemetria verificável |
| F7 | Testes fim a fim, falhas, idempotência, tenant, k6 50 RPS | Resultados reais reportados honestamente |
| F8 | Rastreabilidade, lacunas, revisão do ponto de vista da banca | Auditoria concluída; **G2 aprovado pelo responsável em 2026-10-29** |

Reduzir escopo opcional antes de comprometer requisitos originais ou segurança de produção; declarar gaps honestamente.
