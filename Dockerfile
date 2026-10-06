# Build with the SDK, run on the chiseled ASP.NET Core runtime: no shell, no package manager, and a
# non-root user (UID 1654) by default - which is what the restricted Pod Security Standard of the
# team namespaces asks for.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AksHello/AksHello.csproj AksHello/
RUN dotnet restore AksHello/AksHello.csproj
COPY src/AksHello/ AksHello/
RUN dotnet publish AksHello/AksHello.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# The runtime images listen on 8080 by default.
EXPOSE 8080
USER 1654
ENTRYPOINT ["dotnet", "AksHello.dll"]
