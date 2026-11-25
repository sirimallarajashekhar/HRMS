# syntax=docker/dockerfile:1
# Build context is the repository root:  docker build -f src/Host/Hrms.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src ./src
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Host/Hrms.Api/Hrms.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Non-root user provided by the base image.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Hrms.Api.dll"]
