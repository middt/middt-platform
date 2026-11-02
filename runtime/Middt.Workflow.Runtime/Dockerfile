# Build from the repository root:
#   docker build -f runtime/Middt.Workflow.Runtime/Dockerfile -t middt-workflow-runtime .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY sdk/shared/Middt.Framework.Model/*.csproj sdk/shared/Middt.Framework.Model/
COPY sdk/shared/Middt.Framework.Expressions/*.csproj sdk/shared/Middt.Framework.Expressions/
COPY sdk/shared/Middt.Framework.Ui.Abstractions/*.csproj sdk/shared/Middt.Framework.Ui.Abstractions/
COPY sdk/server/Middt.Framework.Common/*.csproj sdk/server/Middt.Framework.Common/
COPY sdk/server/Middt.Framework.Api/*.csproj sdk/server/Middt.Framework.Api/
COPY sdk/server/Middt.Framework.Rebus/*.csproj sdk/server/Middt.Framework.Rebus/
COPY sdk/server/Middt.Framework.Workflow/*.csproj sdk/server/Middt.Framework.Workflow/
COPY sdk/server/Middt.Framework.Agents/*.csproj sdk/server/Middt.Framework.Agents/
COPY sdk/server/Middt.Framework.Ui.Server/*.csproj sdk/server/Middt.Framework.Ui.Server/
COPY runtime/Middt.Workflow.Runtime/*.csproj runtime/Middt.Workflow.Runtime/
RUN dotnet restore runtime/Middt.Workflow.Runtime/Middt.Workflow.Runtime.csproj

COPY sdk/shared/ sdk/shared/
COPY sdk/server/ sdk/server/
COPY runtime/Middt.Workflow.Runtime/ runtime/Middt.Workflow.Runtime/
RUN dotnet publish runtime/Middt.Workflow.Runtime/Middt.Workflow.Runtime.csproj -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
USER $APP_UID
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Middt.Workflow.Runtime.dll"]

