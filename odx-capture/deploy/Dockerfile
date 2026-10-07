# odx-orchestrator for a Linux server, built from source; compose.yaml builds it with odx-capture/ as the context.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY capture-cs/Directory.Build.props capture-cs/
COPY capture-cs/src/Odx.Core capture-cs/src/Odx.Core
COPY capture-cs/src/Odx.Protocol capture-cs/src/Odx.Protocol
COPY capture-cs/src/Odx.Orchestrator capture-cs/src/Odx.Orchestrator
COPY odx-capture.json .
ARG VERSION=0.1.0
RUN dotnet publish capture-cs/src/Odx.Orchestrator -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY games games
# The state (a bind mount) goes to /data; the image's non-root "app" user (uid 1654) must own it.
RUN mkdir -p /data && chown app /data
USER app
EXPOSE 5180 12300/udp
ENTRYPOINT ["dotnet"]
