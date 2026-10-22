# One Dockerfile for every .NET service. Pick the service with the PROJECT and ENTRY build args (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props ./
COPY contracts/ contracts/
COPY backend/ backend/
# sharing=locked, because compose builds several images in parallel and they mustn't write to the cache at the same time.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "$PROJECT" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# Npgsql looks for the Kerberos (GSSAPI) library at start-up, and the slim image doesn't include it.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The db-migrator looks for db/migrations next to itself. The other services just ignore this folder.
COPY db/ db/
ARG ENTRY
# Use the base image's port 8080 setting. ASPNETCORE_URLS would override it and print a warning on every start.
ENV ENTRY_DLL=${ENTRY} \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_ENVIRONMENT=Production
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$ENTRY_DLL\" \"$@\"", "--"]
