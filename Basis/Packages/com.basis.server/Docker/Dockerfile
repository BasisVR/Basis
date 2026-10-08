# Stage 1: Build the server
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Copy the entire server project (including .props, Contrib, LiteNetLib, etc.)
COPY ["./", "Server/"]

WORKDIR "/src/Server/BasisServerConsole"

# Restore dependencies and publish the application
RUN dotnet restore ./BasisNetworkConsole.csproj
RUN dotnet publish -c Release --no-restore -o /app/publish

# Stage 2: Create runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble

# Install Curl for healthcheck
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy the published output
COPY --from=build /app/publish .

# Basic environment variables (override with a config.xml mount or runtime env)
# On a brand-new server (no config.xml yet) the console can't run the interactive
# first-time setup wizard, so set BasisFirstAdmin to the admin's player UUID (DID)
# to seed one. Comma-separate for several, e.g.:
#   -e BasisFirstAdmin=did:key:z6Mk...
ENV SetPort=4296 \
    PromethusPort=1234 \
    HealthCheckPort=10666 \
    UsingLoggingFile=false \
    EnableStatistics=true \
    EnableConsole=false \
    Password=default_password \
    PeerLimit=1024

# Expose UDP port for game traffic and TCP port for health checks
EXPOSE ${SetPort}/udp
EXPOSE ${SetPort}/tcp
EXPOSE ${HealthCheckPort}/tcp
EXPOSE ${PromethusPort}/tcp

# Run the server
ENTRYPOINT ["dotnet", "BasisNetworkConsole.dll"]
