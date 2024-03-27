FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ManagerConsole/ManagerConsole.csproj", "ManagerConsole/"]
COPY ["AudioFileSorter/AudioFileSorter.csproj", "AudioFileSorter/"]
RUN dotnet restore "ManagerConsole/ManagerConsole.csproj"
COPY . .
WORKDIR "/src/ManagerConsole"
RUN dotnet build "ManagerConsole.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ManagerConsole.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
RUN mkdir "source"
RUN mkdir "destination"
RUN mkdir "export"
VOLUME ["/source", "/destination", "/export"]
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ManagerConsole.dll"]
