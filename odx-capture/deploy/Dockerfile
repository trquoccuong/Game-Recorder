# odx-orchestrator for a Linux server, built from source; compose.yaml builds it with odx-capture/ as the context.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY capture-cs/Directory.Build.props capture-cs/
COPY capture-cs/src/Odx.Core capture-cs/src/Odx.Core
COPY capture-cs/src/Odx.Session capture-cs/src/Odx.Session
COPY capture-cs/src/Odx.Orchestrator capture-cs/src/Odx.Orchestrator
COPY schemas schemas
COPY odx-capture.json .
ARG VERSION=0.1.0
RUN dotnet publish capture-cs/src/Odx.Orchestrator -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY games games
USER app
EXPOSE 5180 12300/udp
ENTRYPOINT ["dotnet"]
