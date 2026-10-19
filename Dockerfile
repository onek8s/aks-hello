# Build with the SDK, run on the Ubuntu ASP.NET Core runtime: a shell (bash, coreutils) for
# `kubectl exec` when debugging (0.6.0; the chiseled image had none), and the non-root user (UID 1654)
# that the restricted Pod Security Standard of the team namespaces asks for.
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
# Network tools for debugging in the pod (0.7.0): ifconfig (net-tools), ping (iputils-ping), curl and
# wget. All of them work as the non-root user with every capability dropped: ping's file capability
# (cap_net_raw=ep) is removed, since the kernel refuses to run such a file in a container without
# CAP_NET_RAW, and ping uses an ICMP datagram socket instead, which the kernel allows for the groups
# in net.ipv4.ping_group_range (the pod sets it; containerd 2 and Docker do by default).
# Keep in step with Dockerfile.arm64.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl wget iputils-ping net-tools \
    && setcap -r /usr/bin/ping \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# HTTP/1.1 on 8080 (the kubelet's probes), h2c on 8081 (the gateway) - see Program.cs.
EXPOSE 8080 8081
USER 1654
ENTRYPOINT ["dotnet", "AksHello.dll"]
