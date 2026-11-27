# The staff panel in a container (docs/ADMIN_PANEL.md §7, T-805). Build from the repository root:
#   docker build -f src/Game.Admin/Dockerfile -t game-admin-panel .
# The image holds the published panel and content/ (names only), never the game server. What may enter the build is in Dockerfile.dockerignore beside this file.
# The container runs as Production: HTTPS only, a certificate, an identity provider, and no development sign-in.
# Pinned images, the server's own (the root Dockerfile): change both together.
ARG DOTNET_SDK=10.0.404-noble
ARG DOTNET_ASPNET=10.0.15-noble

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /src
# The project files first, so the package restore is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Game.Shared/Game.Shared.csproj src/Game.Shared/
COPY src/Game.Content/Game.Content.csproj src/Game.Content/
COPY src/Game.Admin/Game.Admin.csproj src/Game.Admin/
RUN dotnet restore src/Game.Admin/Game.Admin.csproj
COPY src/Game.Shared/ src/Game.Shared/
COPY src/Game.Content/ src/Game.Content/
COPY src/Game.Admin/ src/Game.Admin/
RUN dotnet publish src/Game.Admin/Game.Admin.csproj --configuration Release --no-restore --output /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_ASPNET}
WORKDIR /app
COPY --from=build /out/ ./
COPY content/ ./content/
USER $APP_UID
EXPOSE 8443
# The base image sets a plain-HTTP port of its own; the panel listens where AdminPanel__Listen says, and only there.
ENV ASPNETCORE_HTTP_PORTS="" \
    AdminPanel__ContentDirectory=/app/content
# Everything else is the deployer's (docs/ADMIN_PANEL.md §7): the listen address with PrivateNetwork and AllowedHosts, the certificate, the server, the provider and the secrets.
ENTRYPOINT ["dotnet", "Game.Admin.dll"]
