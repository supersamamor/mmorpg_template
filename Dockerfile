# The game server in a container, for local play (docs/LOCAL_PLAY.md, T-807). Not a production deployment.
#   build:  docker compose --profile play build play-server        (play-local.ps1 does it for you)
# The image holds the published server, content/ and the theme manifests, and nothing else of the repository (.dockerignore).
# Pinned images: the SDK satisfies global.json (10.0.100, latestFeature). Change both tags together.
ARG DOTNET_SDK=10.0.404-noble
ARG DOTNET_ASPNET=10.0.15-noble

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /src
# The project files first, so the package restore is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Game.Shared/Game.Shared.csproj src/Game.Shared/
COPY src/Game.Content/Game.Content.csproj src/Game.Content/
COPY src/Game.Server/Game.Server.csproj src/Game.Server/
RUN dotnet restore src/Game.Server/Game.Server.csproj
COPY src/Game.Shared/ src/Game.Shared/
COPY src/Game.Content/ src/Game.Content/
COPY src/Game.Server/ src/Game.Server/
RUN dotnet publish src/Game.Server/Game.Server.csproj --configuration Release --no-restore --output /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_ASPNET}
WORKDIR /app
COPY --from=build /out/ ./
# The server reads content/, and the asset keys and audio data from themes/ next to it. No art: only the manifests are in the build context.
COPY content/ ./content/
COPY themes/ ./themes/
# The base image's unprivileged user.
USER $APP_UID
EXPOSE 7780
# Applies the migrations (--migrate exits when done), then replaces the shell with the server, so SIGTERM reaches it and it saves every character.
# Arguments given to `docker run` go to the server, not to the migration. Without DOTNET_ENVIRONMENT=Development this is Production: wss:// only.
ENTRYPOINT ["/bin/sh", "-c", "dotnet Game.Server.dll /app/content --migrate && exec dotnet Game.Server.dll /app/content \"$@\"", "game-server"]
