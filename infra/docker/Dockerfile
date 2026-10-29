# Imagem unica parametrizada por projeto (scaffold F2). Uso:
#   docker build -f infra/docker/Dockerfile --build-arg PROJECT=CashFlow.Transactions.Api --target aspnet .
# Alvos: "aspnet" (APIs e worker, que hospeda o Kestrel do listener de saude) e "runtime" (recon-tool, console).
# O contexto de build e a raiz do repositorio.
# Imagens base fixadas por digest do manifest list (docs/testing/evidence/f6/21-image-digests.log, 2026-10-29). Para atualizar:
#   docker buildx imagetools inspect <imagem:tag>, copiar o "Digest:" do topo (nao o de uma plataforma) e revalidar o build.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG PROJECT
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /out /p:UseAppHost=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS aspnet
ARG PROJECT
WORKDIR /app
COPY --from=build /out .
# Caminho estavel para ENTRYPOINT e HEALTHCHECK, independente do nome do projeto.
RUN ln -s "/app/${PROJECT}" /app/entrypoint
# Listen explicito no Kestrel: a imagem nao define portas por URLS/HTTP_PORTS. Escuta em todas as interfaces do container.
ENV ASPNETCORE_HTTP_PORTS="" \
    Hosting__BindAddress=0.0.0.0
USER $APP_UID
ENTRYPOINT ["/app/entrypoint"]

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30 AS runtime
ARG PROJECT
WORKDIR /app
COPY --from=build /out .
RUN ln -s "/app/${PROJECT}" /app/entrypoint
USER $APP_UID
ENTRYPOINT ["/app/entrypoint"]
