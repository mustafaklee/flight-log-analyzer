# syntax=docker/dockerfile:1
# Build context: repository root (see docker-compose.yml).
# Log processing worker; it has no HTTP endpoint. The aspnet runtime is used because Infrastructure needs it.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer is cached while only source files change.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FlightLog.Domain/FlightLog.Domain.csproj src/FlightLog.Domain/
COPY src/FlightLog.LogParsing/FlightLog.LogParsing.csproj src/FlightLog.LogParsing/
COPY src/FlightLog.Application/FlightLog.Application.csproj src/FlightLog.Application/
COPY src/FlightLog.Infrastructure/FlightLog.Infrastructure.csproj src/FlightLog.Infrastructure/
COPY src/FlightLog.Worker/FlightLog.Worker.csproj src/FlightLog.Worker/
RUN dotnet restore src/FlightLog.Worker/FlightLog.Worker.csproj

COPY src/ src/
RUN dotnet publish src/FlightLog.Worker/FlightLog.Worker.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app/publish .
# The aspnet image ships a non-root user named "app".
USER app
ENTRYPOINT ["dotnet", "FlightLog.Worker.dll"]
