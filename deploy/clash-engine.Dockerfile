# Работник движка коллизий: отдельный контейнер, говорит с сервером только по HTTP. Контекст сборки — корень репозитория.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ToadFlow.Contracts/ToadFlow.Contracts.csproj src/ToadFlow.Contracts/
COPY src/ToadFlow.Clash.Worker/ToadFlow.Clash.Worker.csproj src/ToadFlow.Clash.Worker/
RUN dotnet restore src/ToadFlow.Clash.Worker/ToadFlow.Clash.Worker.csproj

COPY src/ToadFlow.Contracts/ src/ToadFlow.Contracts/
COPY src/ToadFlow.Clash.Worker/ src/ToadFlow.Clash.Worker/
RUN dotnet publish src/ToadFlow.Clash.Worker/ToadFlow.Clash.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
# Кеш слепков (том clash-engine-cache): создаётся заранее, чтобы том унаследовал владельца app.
RUN mkdir -p /data/cache && chown app /data/cache
ENV DOTNET_ENVIRONMENT=Production \
    Clash__CacheRoot=/data/cache
USER app
ENTRYPOINT ["dotnet", "ToadFlow.Clash.Worker.dll"]
