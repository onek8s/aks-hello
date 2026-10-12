# Build with the SDK, run on the chiseled ASP.NET Core runtime: no shell, no package manager, and a
# non-root user (UID 1654) by default - which is what the restricted Pod Security Standard of the
# team namespaces asks for.
#
# For a build on a host of the image's own architecture: BuildKit on an amd64 or arm64 runner, or
# the environment's registry for amd64. The registry builds arm64 images on its amd64 host with
# Dockerfile.arm64 instead, which cross-compiles. No `--platform` in FROM: the registry's
# dependency scanner cannot read it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/AksHello/AksHello.csproj AksHello/
RUN dotnet restore AksHello/AksHello.csproj ${TARGETARCH:+-a "$TARGETARCH"}
COPY src/AksHello/ AksHello/
RUN dotnet publish AksHello/AksHello.csproj ${TARGETARCH:+-a "$TARGETARCH"} --no-self-contained --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# The runtime images listen on 8080 by default.
EXPOSE 8080
USER 1654
ENTRYPOINT ["dotnet", "AksHello.dll"]
