# Kea web app. Builds only the web front-end and its core library; the Avalonia
# desktop app and the original Windows Forms project are not part of the image.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, against project files alone, so the layer caches until dependencies change.
COPY src/Kea.Core/Kea.Core.csproj src/Kea.Core/
COPY src/Kea.Web/Kea.Web.csproj src/Kea.Web/
RUN dotnet restore src/Kea.Web/Kea.Web.csproj

COPY src/Kea.Core/ src/Kea.Core/
COPY src/Kea.Web/ src/Kea.Web/
RUN dotnet publish src/Kea.Web/Kea.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl is not in the aspnet runtime image, and the health check below needs it.
# Runs unprivileged: the service writes only to the library volume.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 10001 kea \
    && mkdir -p /library \
    && chown -R kea:kea /library

COPY --from=build --chown=kea:kea /app ./

USER kea

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    KEA_LIBRARYPATH=/library

EXPOSE 8080
VOLUME ["/library"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "Kea.Web.dll"]
