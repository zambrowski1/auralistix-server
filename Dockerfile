# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Auralistix.Server/Auralistix.Server.csproj Auralistix.Server/
RUN dotnet restore Auralistix.Server/Auralistix.Server.csproj

COPY Auralistix.Server/ Auralistix.Server/
WORKDIR /src/Auralistix.Server
RUN dotnet publish Auralistix.Server.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Database__Provider=Postgres
ENV Community__StorageProvider=S3
ENV S3__ServiceUrl=https://s3.twcstorage.ru
ENV S3__Region=ru-1
ENV S3__KeyPrefix=community
ENV S3__ForcePathStyle=true

EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Auralistix.Server.dll"]
