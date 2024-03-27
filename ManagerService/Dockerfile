FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ManagerService/ManagerService.csproj", "ManagerService/"]
COPY ["AudioFileSorter/AudioFileSorter.csproj", "AudioFileSorter/"]
RUN dotnet restore "ManagerService/ManagerService.csproj"
COPY . .
WORKDIR "/src/ManagerService"
RUN dotnet build "ManagerService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ManagerService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
ENV export "/app/export/"
ENV source "/app/source"
ENV destination "/app/destination"
RUN mkdir "source"
RUN mkdir "destination"
RUN mkdir "export"
VOLUME ["/source", "/destination", "/export"]
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ManagerService.dll"]
