FROM node:24 AS frontend-build
WORKDIR /src/electron-ui

COPY electron-ui/package*.json ./
RUN npm ci

COPY electron-ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src

COPY AudioFileSorter/AudioFileSorter.csproj AudioFileSorter/
COPY ManagerApi/ManagerApi.csproj ManagerApi/
RUN dotnet restore ManagerApi/ManagerApi.csproj

COPY AudioFileSorter/ AudioFileSorter/
COPY ManagerApi/ ManagerApi/
COPY --from=frontend-build /src/electron-ui/dist/ ManagerApi/wwwroot/

WORKDIR /src/ManagerApi
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=backend-build /app/publish ./

# The backend listens on loopback unless told otherwise; a container has to accept connections
# from outside itself for the published port to work.
ENV ASPNETCORE_URLS=http://0.0.0.0:5123
# The base image also sets HTTP_PORTS=8080; clearing it stops ASP.NET warning at every start that
# ASPNETCORE_URLS overrides it.
ENV ASPNETCORE_HTTP_PORTS=
# Keeps the choices made on the Sort page (update check, copy speed, automatic sorting) and when
# the last automatic sort ran, across container restarts.
ENV OABO_SETTINGS_PATH=/data/settings.json
EXPOSE 5123

# Only /data. Declaring /source and /destination as volumes would make Docker put an empty, hidden
# volume there whenever a mount is forgotten or mistyped: the folder would show as Found and a
# whole library could be copied where nobody can see it, and lost with the container. Left
# undeclared, a missing mount shows as "not found inside the container".
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "ManagerApi.dll"]
