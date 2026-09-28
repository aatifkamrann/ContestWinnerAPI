## Built with src/ as the context (docker-compose.yml), because the API is
## the top of five projects that all have to be in the image's build.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Project files first, so a code change does not repeat the restore.
COPY WinnersPortal.Domain/WinnersPortal.Domain.csproj WinnersPortal.Domain/
COPY WinnersPortal.Infrastructure/WinnersPortal.Infrastructure.csproj WinnersPortal.Infrastructure/
COPY WinnersPortal.Migrations.SqlServer/WinnersPortal.Migrations.SqlServer.csproj WinnersPortal.Migrations.SqlServer/
COPY WinnersPortal.Services/WinnersPortal.Services.csproj WinnersPortal.Services/
COPY WinnersPortal.Api/WinnersPortal.Api.csproj WinnersPortal.Api/
RUN dotnet restore WinnersPortal.Api/WinnersPortal.Api.csproj
COPY WinnersPortal.Domain/ WinnersPortal.Domain/
COPY WinnersPortal.Infrastructure/ WinnersPortal.Infrastructure/
COPY WinnersPortal.Migrations.SqlServer/ WinnersPortal.Migrations.SqlServer/
COPY WinnersPortal.Services/ WinnersPortal.Services/
COPY WinnersPortal.Api/ WinnersPortal.Api/
RUN dotnet publish WinnersPortal.Api/WinnersPortal.Api.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WinnersPortal.Api.dll"]
