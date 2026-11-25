# syntax=docker/dockerfile:1
# Build context is the repository root:  docker build -f src/Host/Hrms.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src ./src
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Host/Hrms.Worker/Hrms.Worker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:11.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Hrms.Worker.dll"]
