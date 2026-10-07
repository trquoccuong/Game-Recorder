# odx-orchestrator and odx-merge for a Linux server, built from source into one image; compose.yaml builds it with
# odx-capture/ as the context and starts each with its own command.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY capture-cs/Directory.Build.props capture-cs/
COPY capture-cs/src/Odx.Core capture-cs/src/Odx.Core
COPY capture-cs/src/Odx.Session capture-cs/src/Odx.Session
COPY capture-cs/src/Odx.Orchestrator capture-cs/src/Odx.Orchestrator
COPY capture-cs/src/Odx.Merge capture-cs/src/Odx.Merge
COPY schemas schemas
COPY odx-capture.json .
ARG VERSION=0.1.0
RUN dotnet publish capture-cs/src/Odx.Orchestrator -c Release -o /app -p:Version=$VERSION && \
    dotnet publish capture-cs/src/Odx.Merge -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY games games
# Sessions go to /data (a bind mount); the image's non-root "app" user (uid 1654) must own it.
RUN mkdir -p /data && chown app /data
USER app
EXPOSE 5180 5190 12300/udp
ENTRYPOINT ["dotnet"]
