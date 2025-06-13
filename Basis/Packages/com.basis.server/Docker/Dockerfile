# Stage 1: Build the server
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy the entire server project (including .props, Contrib, LiteNetLib, etc.)
COPY ["../", "Server/"]

WORKDIR "/src/Server/BasisServerConsole"

# Restore dependencies and publish the application
RUN dotnet restore BasisNetworkConsole.csproj
RUN dotnet publish -c Release -o /app/publish

# Stage 2: Create runtime image
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app

# Copy the published output
COPY --from=build /app/publish .

# Basic environment variables (override with a config.xml mount or runtime env)
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
EXPOSE ${HealthCheckPort}/tcp
EXPOSE ${PromethusPort}/tcp

# Run the server
ENTRYPOINT ["dotnet", "BasisNetworkConsole.dll"] 