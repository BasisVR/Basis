# escape=`
FROM mcr.microsoft.com/windows/servercore:ltsc2022

WORKDIR C:\app
COPY ./HeadlessWindows64/. .
RUN cat <<'EOF' > ./HeadlessLinuxServer_Data/config.xml
<?xml version="1.0" encoding="utf-8"?>
<Configuration>
  <Password>default_password</Password>
  <Ip>server1.basisvr.org</Ip>
  <Port>4296</Port>
</Configuration>
EOF

ENTRYPOINT ["C:\\app\\HeadlessWindowsServer.exe"]
