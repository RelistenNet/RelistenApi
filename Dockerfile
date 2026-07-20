FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY RelistenApi/RelistenApi.csproj RelistenApi/
RUN dotnet restore RelistenApi/RelistenApi.csproj

COPY RelistenApi/ RelistenApi/
RUN dotnet publish RelistenApi/RelistenApi.csproj -c Release -o /app/publish --no-restore

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

ENV ASPNETCORE_URLS=http://+:3823 \
    DOTNET_EnableDiagnostics=0

RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin appuser

COPY --from=build /app/publish/ ./
RUN test -f /app/RelistenApi.dll

EXPOSE 3823

USER appuser

ENTRYPOINT ["dotnet", "RelistenApi.dll"]
