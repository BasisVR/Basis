FROM mcr.microsoft.com/windows/servercore:ltsc2022

WORKDIR C:\app
COPY ./HeadlessWindows64/. .
EXPOSE 10666

ENTRYPOINT ["C:\\app\\HeadlessWindowsServer.exe"]
