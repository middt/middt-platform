# Build from the repository root:
#   docker build -f demo/Middt.Demo.Workflows/Dockerfile -t middt-demo-workflows .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY demo/Middt.Demo.Workflows/*.csproj demo/Middt.Demo.Workflows/
RUN dotnet restore demo/Middt.Demo.Workflows/Middt.Demo.Workflows.csproj

COPY demo/Middt.Demo.Workflows/ demo/Middt.Demo.Workflows/
RUN dotnet publish demo/Middt.Demo.Workflows/Middt.Demo.Workflows.csproj -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Middt.Demo.Workflows.dll"]

