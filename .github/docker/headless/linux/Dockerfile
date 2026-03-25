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
RUN cat <<'EOF' > ./HeadlessLinuxServer_Data/config.xml
<?xml version="1.0" encoding="utf-8"?>
<Configuration>
  <Password>default_password</Password>
  <Ip>server1.basisvr.org</Ip>
  <Port>4296</Port>
</Configuration>
EOF
RUN chmod +x ./HeadlessLinuxServer.x86_64

ENTRYPOINT ["./HeadlessLinuxServer.x86_64"]
