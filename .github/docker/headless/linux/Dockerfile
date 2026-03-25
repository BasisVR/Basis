FROM ubuntu:22.04

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ca-certificates \
    libglib2.0-0 \
    libgtk-3-0 \
    libx11-6 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY ./HeadlessLinux64/. .
RUN chmod +x ./HeadlessLinuxServer.x86_64

ENTRYPOINT ["./HeadlessLinuxServer.x86_64"]
