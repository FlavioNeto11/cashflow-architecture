# ADR-0008 — Versão do .NET para o protótipo

- **Status:** ACEITO em G1 (2026-10-29); conteúdo PROPOSTO em F1. Responde a DEC-10.
- **Data:** 2026-10-29 (registro de G1).
- **Decisor/revisão humana:** Flavio Padilha Neto (gate G1).

## Contexto e requisito

Fatos verificados pelo líder em 2026-10-28 (`docs/testing/evidence-log.md`), não reverificados nesta fase:

- SDKs instalados: 8.0.424, 9.0.316, 9.0.317.
- **.NET 8 e .NET 9 encerram suporte em 2026-11-10** (blog oficial .NET).
- **.NET 10 LTS não está instalado**; instalar é mudança de ambiente que exige autorização do responsável.
- **Nota pós-G1:** G1-08 aprovou a opção (a); o SDK 10.0.403 foi instalado e fixado em `global.json` (`docs/testing/evidence-log.md`, F2).
- Diretriz do projeto: escolher versão LTS pela instalação e suporte atual antes de criar o projeto.
- A defesa perante a banca ocorrerá, provavelmente, após 2026-11-10.

## Alternativas consideradas

| Opção | Benefícios | Custos / riscos |
|---|---|---|
| **(a)** Instalar .NET 10 SDK (LTS) e usar TFM `net10.0` | Suporte durante toda a vida útil do protótipo e da defesa; evita a pergunta "por que entregou em runtime sem suporte?"; mesmas APIs centrais usadas pelo desenho (Kestrel client certs, rate limiting, `BackgroundService`, EF Core, OpenTelemetry) | Exige autorização e download/instalação; verificação `dotnet --list-sdks` após instalar (evidência de F2); imagens base `mcr.microsoft.com/dotnet/aspnet:10.0` a confirmar; eventual ajuste de pacotes para versões compatíveis |
| **(b)** Usar .NET 9 já instalado (TFM `net9.0`), declarando fim de suporte em **2026-11-10** | Nenhuma mudança de ambiente; começa imediatamente | Entrega sobre runtime sem suporte a partir de 2026-11-10 (13 dias após a data desta proposta); risco de defesa e de segurança a declarar; migração posterior para `net10.0` é pequena se o código evitar APIs específicas de versão, mas é trabalho adicional |
| **(b')** Usar .NET 8 (LTS) já instalado | Rótulo "LTS" | Mesmo fim de suporte (2026-11-10); mais antigo que o 9; nenhuma vantagem sobre (b) |

## Decisão recomendada

**(a)**, condicionada à autorização explícita do responsável para instalar o SDK .NET 10 LTS. Procedimento proposto: instalar pelo instalador/`winget` oficial da Microsoft; registrar `dotnet --list-sdks` no `evidence-log.md` em F2; fixar `global.json` com a versão instalada.

**Fallback (b)** se a autorização não for concedida: TFM `net9.0`, `global.json` fixando 9.0.317, e declaração explícita em `README`, `implemented.md` e na defesa de que o runtime encerra suporte em 2026-11-10, com plano de migração para `net10.0` (troca de TFM e de imagens base; pacotes compatíveis).

Em ambos os casos: `decimal` para dinheiro, EF Core com migrações versionadas, `BackgroundService` para publisher/consumidor, Problem Details, `dotnet format` e analyzers (sem afirmar execução antes de executar).

## Consequências e trade-offs

- (a) adiciona um passo administrativo antes de F2 e uma verificação; elimina um risco de defesa.
- (b) remove o passo, mas converte um risco técnico em risco de credibilidade que precisa ser dito em voz alta na banca.
- Nenhuma característica específica de versão é afirmada aqui como verificada; compatibilidade de pacotes (EF Core, OpenTelemetry, YARP, RabbitMQ client) será confirmada em F2 com `dotnet restore` real.

## Validação e evidências previstas

- F2: saída de `dotnet --list-sdks`, `dotnet --version` e `dotnet build` registradas em `evidence-log.md`.
- `global.json` versionado.

## Registro de aprovação

- G1 aprovado por **Flavio Padilha Neto** em 2026-10-29T02:31:29Z (registro em `docs/project/approvals.md`). Nota registrada: "Arquitetura opcao C aprovada; G1-10 opcao (b); demais itens G1 conforme padrao proposto em decisions-needed.md". Aprovação do desenho; nenhuma implementação ou validação está implícita.
