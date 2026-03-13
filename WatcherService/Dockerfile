# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so the restore layer is cached
COPY AudioFileSorter/AudioFileSorter.csproj AudioFileSorter/
COPY WatcherService/WatcherService.csproj   WatcherService/
RUN dotnet restore WatcherService/WatcherService.csproj

# Copy the full source and publish a self-contained single file
COPY AudioFileSorter/ AudioFileSorter/
COPY WatcherService/  WatcherService/
WORKDIR /src/WatcherService
RUN dotnet publish -c Release -o /app/publish

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Volumes expected at runtime (override with bind mounts in docker-compose)
VOLUME ["/data", "/source", "/destination"]

ENTRYPOINT ["dotnet", "WatcherService.dll"]
