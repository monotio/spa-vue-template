# Multi-stage production image: Node builds the SPA, the .NET SDK publishes
# the API, the ASP.NET runtime serves both (SPA from wwwroot with the
# index.html fallback). Build:  docker build -t vueapp1 .
#                      Run:    docker run -p 8080:8080 vueapp1

FROM node:24-alpine AS client-build
WORKDIR /src
COPY package.json package-lock.json .npmrc ./
COPY vueapp1.client/package.json vueapp1.client/
RUN npm ci
COPY vueapp1.client/ vueapp1.client/
RUN npm run build-only -w vueapp1.client

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
# BannedSymbols.txt is wired in via Directory.Build.props (AdditionalFiles);
# the compiler hard-fails (CS2001) without it.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props BannedSymbols.txt ./
COPY VueApp1.Server/VueApp1.Server.csproj VueApp1.Server/packages.lock.json VueApp1.Server/
# ExcludeSpaReference: the SPA is built in the node stage above; without it
# the static-web-assets pipeline evaluates the esproj and demands Node.js.
RUN dotnet restore VueApp1.Server/VueApp1.Server.csproj --locked-mode -p:ExcludeSpaReference=true
COPY VueApp1.Server/ VueApp1.Server/
# Copy the built SPA into wwwroot BEFORE publish: the static-web-assets
# pipeline then ingests it into the publish manifest, which buys max-quality
# build-time brotli/gzip with content negotiation, per-encoding content-based
# ETags, and correct cache headers — none of which the post-publish-copy
# approach had (runtime compression cost more CPU for ~34% larger payloads).
COPY --from=client-build /src/vueapp1.client/dist/ VueApp1.Server/wwwroot/
RUN dotnet publish VueApp1.Server/VueApp1.Server.csproj -c Release -o /app --no-restore -p:ExcludeSpaReference=true

# Chiseled (distroless-style) runtime: no shell, no package manager, ~100 MB
# smaller uncompressed — a pure attack-surface reduction over plain aspnet:10.0.
# The -extra variant is REQUIRED, not optional: it retains ICU + tzdata, and
# this app does not set InvariantGlobalization (culture-sensitive formatting
# has bitten this repo before — see the Server-Timing invariant-culture fix).
# No shell also means: no `docker exec` debugging (swap the base back to
# aspnet:10.0 temporarily when you need one) and no curl/shell HEALTHCHECK —
# orchestrators probe /health/live and /health/ready over HTTP instead.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=server-build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Chiseled images already run as the non-root app user; keeping USER explicit
# preserves non-root if the base is ever swapped back for debugging.
USER $APP_UID
ENTRYPOINT ["dotnet", "VueApp1.Server.dll"]
