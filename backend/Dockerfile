# One image for every .NET service; choose it with the PROJECT and ENTRY build args (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props ./
COPY contracts/ contracts/
COPY backend/ backend/
# sharing=locked: compose builds several images in parallel and they must not write the cache at once.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "$PROJECT" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# Npgsql probes for the Kerberos (GSSAPI) library at start-up; the slim image does not ship it.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The db-migrator looks for db/migrations next to it; other services ignore this folder.
COPY db/ db/
ARG ENTRY
# Use the base image's port 8080 setting; ASPNETCORE_URLS would override it and warn on every start.
ENV ENTRY_DLL=${ENTRY} \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_ENVIRONMENT=Production
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$ENTRY_DLL\" \"$@\"", "--"]
