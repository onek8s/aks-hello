# Build with the SDK, run on the chiseled ASP.NET Core runtime: no shell, no package manager, and a
# non-root user (UID 1654) by default - which is what the restricted Pod Security Standard of the
# team namespaces asks for.
#
# Cross-compiled: the SDK runs on the build host's own architecture and publishes for the target
# one, so an arm64 image never runs the compiler under emulation (it crashes there). BuildKit sets
# BUILDPLATFORM and TARGETARCH by itself; the classic builder of an ACR task gets TARGETARCH as a
# build argument (onek8s/aks-terraform, container_registry_builds) and builds on linux/amd64.
ARG BUILDPLATFORM=linux/amd64
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
WORKDIR /src
COPY src/AksHello/AksHello.csproj AksHello/
RUN dotnet restore AksHello/AksHello.csproj -a "$TARGETARCH"
COPY src/AksHello/ AksHello/
RUN dotnet publish AksHello/AksHello.csproj -a "$TARGETARCH" --no-self-contained --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# The runtime images listen on 8080 by default.
EXPOSE 8080
USER 1654
ENTRYPOINT ["dotnet", "AksHello.dll"]
