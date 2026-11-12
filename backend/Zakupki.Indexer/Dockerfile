FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Zakupki.Indexer.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production
RUN mkdir -p /data/orders

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Zakupki.Indexer.dll"]
