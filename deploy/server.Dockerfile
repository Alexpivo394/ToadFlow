# Сервер ToadFlow (ядро + модули). Контекст сборки — корень репозитория.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ToadFlow.Contracts/ToadFlow.Contracts.csproj src/ToadFlow.Contracts/
COPY src/ToadFlow.Server/ToadFlow.Server.csproj src/ToadFlow.Server/
COPY src/ToadFlow.Server.Export/ToadFlow.Server.Export.csproj src/ToadFlow.Server.Export/
COPY src/ToadFlow.Server.Loi/ToadFlow.Server.Loi.csproj src/ToadFlow.Server.Loi/
COPY src/ToadFlow.Server.Clash/ToadFlow.Server.Clash.csproj src/ToadFlow.Server.Clash/
COPY src/ToadFlow.Server.Host/ToadFlow.Server.Host.csproj src/ToadFlow.Server.Host/
RUN dotnet restore src/ToadFlow.Server.Host/ToadFlow.Server.Host.csproj

COPY src/ src/
RUN dotnet publish src/ToadFlow.Server.Host/ToadFlow.Server.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Хранилище выгрузок для коллизий (том clash в docker compose): создаётся заранее, чтобы том унаследовал владельца app.
RUN mkdir -p /data/clash && chown app /data/clash
USER app
ENTRYPOINT ["dotnet", "ToadFlow.Server.Host.dll"]
