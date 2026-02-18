# Multi-stage build for ObservabilityAssistant.Console
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# copy solution and restore
COPY ObservabilityAssistant.sln ./
COPY src/ObservabilityAssistant.Core/ObservabilityAssistant.Core.csproj src/ObservabilityAssistant.Core/
COPY src/ObservabilityAssistant.MCPServers/ObservabilityAssistant.MCPServers.csproj src/ObservabilityAssistant.MCPServers/
COPY src/ObservabilityAssistant.Console/ObservabilityAssistant.Console.csproj src/ObservabilityAssistant.Console/
RUN dotnet restore ObservabilityAssistant.sln

# copy everything and publish
COPY . .
RUN dotnet publish src/ObservabilityAssistant.Console/ObservabilityAssistant.Console.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ObservabilityAssistant.Console.dll"]
