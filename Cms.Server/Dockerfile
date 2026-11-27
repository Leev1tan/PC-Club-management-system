# Cms.Server - ASP.NET Core 8 API
# Build from the repository root (Cms.Server references Cms.Shared):
#   docker build -f Cms.Server/Dockerfile -t ccms-server .
#   docker run --rm -p 8080:8080 -e DEMO_MODE=true ccms-server     # demo with built-in SQLite
#
# Runtime settings (environment variables):
#   ConnectionStrings__DefaultConnection  PostgreSQL connection string; empty = SQLite in /app/data
#   Database__Provider                    postgres | sqlite (optional, overrides auto-detection)
#   CMS_INITIAL_OWNER_USERNAME / _PASSWORD  first Owner account (only while no staff account exists);
#                                         without them: setup screen + code in /app/data/setup-code.txt
#   CMS_ENROLLMENT_TOKEN                  secret for the agent installer (-EnrollmentToken)
#   DEMO_MODE / DEMO_RESET_HOURS          demo club + simulated PCs + demo staff accounts, reset interval (default 6 h)
#   Cors__AllowedOrigins                  only if the admin UI is served from another origin
#   Auth__CookieSecure=always             login cookie only over HTTPS (set it behind an HTTPS ingress)
#   ReverseProxy__ForwardLimit            proxies in front of the server (default 1; 2 = ingress + admin-ui)
# Keep /app/data on a volume: login cookie keys, setup code, SQLite database.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, so the package layer is cached while only sources change
COPY Cms.Shared/Cms.Shared.csproj Cms.Shared/
COPY Cms.Server/Cms.Server.csproj Cms.Server/
RUN dotnet restore Cms.Server/Cms.Server.csproj

COPY Cms.Shared/ Cms.Shared/
COPY Cms.Server/ Cms.Server/
RUN dotnet publish Cms.Server/Cms.Server.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    Storage__DataDirectory=/app/data \
    Database__SqlitePath=/app/data/cms.db \
    Discovery__Enabled=false \
    ReverseProxy__TrustForwardedHeaders=true

COPY --from=build /app/publish .
# Writable directory for the optional SQLite database; everything else stays read-only for the app user
RUN mkdir -p /app/data /app/wwwroot && chown "$APP_UID" /app/data

# Non-root user (UID 1654) provided by the official .NET images
USER $APP_UID
EXPOSE 8080

# The image has no curl/wget; bash /dev/tcp is enough for a liveness check
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health HTTP/1.0\r\n\r\n" >&3 && grep -q "200 OK" <&3' || exit 1

ENTRYPOINT ["dotnet", "Cms.Server.dll"]
