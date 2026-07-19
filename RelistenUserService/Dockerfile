FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY Relisten.Accounts.Contracts/Relisten.Accounts.Contracts.csproj Relisten.Accounts.Contracts/
COPY RelistenUserService/RelistenUserService.csproj RelistenUserService/
RUN dotnet restore RelistenUserService/RelistenUserService.csproj

COPY Relisten.Accounts.Contracts/ Relisten.Accounts.Contracts/
COPY RelistenUserService/ RelistenUserService/
RUN dotnet publish RelistenUserService/RelistenUserService.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ARG BUILD_DATE
ARG VCS_REF
ARG VERSION
ARG SOURCE

LABEL org.opencontainers.image.created=$BUILD_DATE \
      org.opencontainers.image.revision=$VCS_REF \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.source=$SOURCE

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin appuser

COPY --from=build /app/publish/ ./
RUN test -f /app/RelistenUserService.dll

EXPOSE 8080

USER appuser

ENTRYPOINT ["dotnet", "RelistenUserService.dll"]
