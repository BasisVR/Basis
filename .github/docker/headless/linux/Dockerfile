FROM ubuntu:22.04

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ca-certificates \
    libglib2.0-0 \
    libgtk-3-0 \
    libx11-6 \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
ARG ARTIFACT_DIR=HeadlessLinux64
COPY ./${ARTIFACT_DIR}/. .
EXPOSE 10666/tcp
RUN set -eux; \
    exe="$(find . -maxdepth 1 -type f \( -name 'HeadlessLinuxServer.x86_64' -o -name 'HeadlessLinuxServer.arm64' -o -name 'HeadlessLinuxServer' \) | head -n1)"; \
    test -n "$exe"; \
    chmod +x "$exe"; \
    ln -sf "$(basename "$exe")" /app/HeadlessLinuxServer

ENTRYPOINT ["./HeadlessLinuxServer"]
