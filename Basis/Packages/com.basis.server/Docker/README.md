# Basis Server Docker Setup

This document guides you through setting up and running the Basis server, using Docker and Docker Compose.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Directory Structure](#directory-structure)
- [Configuration](#configuration)
- [Docker Compose Configuration](#docker-compose-configuration)
- [Getting Started](#getting-started)
- [Customizing Configuration](#customizing-configuration)
- [Volumes and Persistence](#volumes-and-persistence)
- [Logging and Monitoring](#logging-and-monitoring)
- [Troubleshooting](#troubleshooting)
- [License](#license)

## Prerequisites

- Docker (latest version recommended)
- Docker Compose (v2 syntax: `docker compose`)

## Directory Structure

When you first run the server using `docker compose up`, the following directories will be created within the `Docker/` folder if they do not already exist:

```text
Docker/
├── config/
└── initialresources/
```

- `config/`: Contains XML configuration files for the server (e.g., main settings, admin lists, ban lists). The server may auto-generate or update these based on its internal defaults and environment variables.
- `initialresources/`: Contains static assets required by the server at runtime. These are typically mounted as read-only into the container.

## Configuration

The server's behavior is primarily controlled by environment variables when running with Docker, which can override settings that might otherwise be loaded from or written to XML configuration files.

### Configuration Files

Upon startup, or if not present, the server may generate default configuration files in the `config/` directory (mounted from `./config` as per `docker-compose.yml`):

- `config/config.xml`: Main server settings (ports, timeouts, peer limits, authentication, etc.).
- `config/admins.xml`: List of admin user identifiers.
- `config/banned_players.xml`: List of banned user identifiers.

Example snippet from a `config/config.xml` (values here might be defaults before environment variables are applied):
```xml
<Configuration>
  <PeerLimit>1024</PeerLimit>
  <SetPort>4296</SetPort>
  <EnableConsole>true</EnableConsole>
  <DisallowHeadless>false</DisallowHeadless>
  <!-- ... other settings ... -->
</Configuration>
```

### Environment Variables

Key settings can be overridden or set via environment variables in your `docker-compose.yml` file or `docker run` command. These take precedence.

Commonly used environment variables:

| Environment Variable | Default in `docker-compose.yml` | Description                                       |
| -------------------- | ------------------------------- | ------------------------------------------------- |
| `SetPort`            | `4296`                          | UDP port for game client traffic.                 |
| `HealthCheckPort`    | `10666`                         | TCP port for server health checks.                |
| `PromethusPort`      | `1234`                          | TCP port for Prometheus metrics.                  |
| `PeerLimit`          | `1024`                          | Maximum number of concurrent connected peers.     |
| `Password`           | `default_password`              | Connection password for clients. **Change this!** |
| `EnableStatistics`   | `true`                          | Enables the statistics module.                    |
| `EnableConsole`      | `false`                         | Enables the interactive server console (CLI).     |
| `DisallowHeadless`   | `false`                         | Disconnects connected headless clients and blocks new ones. |
| `NetworkStackId`     | (unset, UDP only)               | Transports to listen on. With the WebSocket transport package installed, `litenetlib,websocket` also accepts web browsers over TCP. |

A more comprehensive list of configurable settings can typically be found by inspecting the generated `config/config.xml` after an initial run, or by checking the server's internal documentation if available.

### Web Clients (WebSocket)

Browsers cannot use UDP, so web builds of Basis connect over WebSocket. WebSocket support is a separate package, `com.basis.transport.websocket`, that Basis does not include. Install it on the server with the Basis Package Manager (`basispm --project <repo> server-install <its git URL>`); the image build then restores it from git like any other server package (a `file:` path outside `Basis Server` is not in the Docker build context). With it installed, the server can listen on both at once: set `NetworkStackId` to `litenetlib,websocket` and every player, whichever transport they used, joins the same world.

1. Set `NetworkStackId: litenetlib,websocket` in the `environment` block (or `<NetworkStackId>litenetlib,websocket</NetworkStackId>` in `config/config.xml`).
2. Publish the same port number over TCP as well as UDP: `- "4296:4296/tcp"`. The WebSocket listener uses the game port number unless `config/transports/websocket.xml` sets its own `Port`.
3. A page served over `https://` may only open `wss://` connections. Either put a TLS-terminating reverse proxy in front of the server (Caddy, nginx, Traefik) and forward it to the container's TCP port, or set `TlsEnabled`, `TlsCertificatePath` and `TlsKeyPath` in `config/transports/websocket.xml` (a mounted Let's Encrypt `fullchain.pem` and `privkey.pem` work; the file is reloaded when it changes).
4. Behind a proxy, add the proxy's address to `TrustedProxies` in `websocket.xml` so the server sees each player's real address in `X-Forwarded-For`. Without it every web player appears to come from the proxy, and an IP ban would hit all of them.

`/health` lists each transport under `transports`, with its peer count and, for WebSocket, pending handshakes and shed updates.

## Docker Compose Configuration

The `docker-compose.yml` file orchestrates the server deployment. Here's the provided example (`Docker/docker-compose.yml`):

```yaml
services:
  basis-server:
    build:
      context: ../ # Build context is the parent directory (Basis Server/)
      dockerfile: Docker/Dockerfile # Path to the Dockerfile
    image: basis-server:latest # Name and tag for the built image
    container_name: basis-server # Custom name for the running container
    restart: unless-stopped # Policy for restarting the container
    environment:
      # Environment variables to configure the server
      SetPort: 4296
      HealthCheckPort: 10666
      PromethusPort: 1234
      Password: default_password # IMPORTANT: Change for production!
      PeerLimit: 1024
      EnableStatistics: true
      EnableConsole: false # Set to true for interactive console debugging
    ports:
      # Mapping host ports to container ports
      - "4296:4296/udp"    # Game traffic
      - "10666:10666/tcp"  # Health checks
      - "1234:1234/tcp"    # Prometheus metrics
    volumes:
      # Mounting host directories into the container
      - ./initialresources:/app/initialresources:ro # Read-only static assets
      - ./config:/app/config                         # Read-write server configuration
```
**Security Note:** The default password `default_password` is set for ease of setup. **You MUST change this** in your `docker-compose.yml` for any non-local or production deployment.

## Getting Started

1.  **Navigate to the Docker directory:**
    Open your terminal and change to the directory containing the `docker-compose.yml` file:
    ```bash
    cd path/to/your/project/Basis\ Server/Docker/
    ```

2.  **Build the Docker image:**
    This command builds the server image using the `Dockerfile`.
    ```bash
    docker compose build
    ```

3.  **Start the server:**
    This command starts the server in detached mode (`-d`), meaning it runs in the background.
    ```bash
    docker compose up -d
    ```
    **Important:** For the first run, ensure `config/` and `initialresources/` directories exist or are created by this process.

4.  **View server logs:**
    To monitor the server's output and check for errors:
    ```bash
    docker compose logs -f basis-server
    ```
    (Use `Ctrl+C` to stop following logs)

5.  **Stop the server:**
    This command stops and removes the containers defined in `docker-compose.yml`.
    ```bash
    docker compose down
    ```

## Customizing Configuration

-   **Environment Variables (Recommended for Docker):**
    Modify the `environment` section in `docker-compose.yml` to change settings like ports, password, peer limit, etc. After changes, you may need to rebuild and/or restart:
    ```bash
    docker compose up -d --build # To rebuild and restart
    # or
    docker compose restart basis-server # To just restart the service if only ENV vars changed
    ```

-   **XML Configuration Files (`config/`):**
    For more advanced settings not exposed via environment variables, you can sometimes edit the XML files in the `config/` directory *while the server is stopped*. The server will load these on its next start. However, be aware that environment variables might still override these.
    After modifying files in `config/`, restart the service:
    ```bash
    docker compose restart basis-server
    ```

## Volumes and Persistence

-   `./config:/app/config`: This volume mounts the `Docker/config/` directory on your host to `/app/config` inside the container. This allows server configuration to persist across container restarts. The server can read from and write to these files.
-   `./initialresources:/app/initialresources:ro`: This mounts `Docker/initialresources/` as read-only into the container. These are static assets the server needs.

## Logging and Monitoring

-   **Docker Logs:** Access real-time logs using `docker compose logs -f basis-server`.
-   **Metrics Endpoint:** If enabled and configured (default: `PromethusPort: 1234`), Prometheus-compatible metrics should be available at `http://<host_ip>:1234/metrics`.
-   **Health Check Endpoint:** If enabled and configured (default: `HealthCheckPort: 10666`), a health check endpoint should be available at `http://<host_ip>:10666/health`.

## Troubleshooting

-   **Server Fails to Start:**
    -   Check logs: `docker compose logs basis-server`. Look for error messages related to port binding, configuration loading, or missing files.
    -   Ensure Docker daemon is running.

-   **Port Conflicts:**
    -   If you see errors like "port is already allocated," another service on your host is using one of the ports (4296/udp, 10666/tcp, 1234/tcp).
    -   Change the conflicting port mapping in `docker-compose.yml` (e.g., `"8080:4296/udp"` to use host port 8080 for game traffic).

-   **Configuration Changes Not Applied:**
    -   If you changed `docker-compose.yml` (e.g., environment variables), you need to stop and restart the services: `docker compose down && docker compose up -d`. Sometimes a `docker compose up -d --force-recreate` or `docker compose restart basis-server` is sufficient.
    -   If you changed the `Dockerfile`, you must rebuild the image: `docker compose build` and then restart.
    -   If you manually edited files in the `config/` volume, ensure the server was stopped and then restarted: `docker compose restart basis-server`.

-   **Interactive Console Not Working:**
    -   The `EnableConsole` environment variable in `docker-compose.yml` must be set to `true`.
    -   You'll need to attach to the container to use it: `docker attach basis-server` (or `docker compose attach basis-server` if supported by your compose version). Detach with `Ctrl+P` then `Ctrl+Q`.

## License

This project is licensed under the MIT License. See the [LICENSE](../../LICENSE) file for details.
