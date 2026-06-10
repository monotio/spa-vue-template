# Multi-stage production image: Node builds the SPA, the .NET SDK publishes
# the API, the ASP.NET runtime serves both (SPA from wwwroot with the
# index.html fallback). Build:  docker build -t vueapp1 .
#                      Run:    docker run -p 8080:8080 vueapp1

FROM node:24-alpine AS client-build
WORKDIR /src/vueapp1.client
COPY vueapp1.client/package.json vueapp1.client/package-lock.json vueapp1.client/.npmrc ./
RUN npm ci
COPY vueapp1.client/ ./
RUN npm run build-only

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY VueApp1.Server/VueApp1.Server.csproj VueApp1.Server/packages.lock.json VueApp1.Server/
# ExcludeSpaReference: the SPA is built in the node stage above; without it
# the static-web-assets pipeline evaluates the esproj and demands Node.js.
RUN dotnet restore VueApp1.Server/VueApp1.Server.csproj --locked-mode -p:ExcludeSpaReference=true
COPY VueApp1.Server/ VueApp1.Server/
RUN dotnet publish VueApp1.Server/VueApp1.Server.csproj -c Release -o /app --no-restore -p:ExcludeSpaReference=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=server-build /app ./
COPY --from=client-build /src/vueapp1.client/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "VueApp1.Server.dll"]
