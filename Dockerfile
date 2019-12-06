FROM mcr.microsoft.com/dotnet/core/sdk:3.1

WORKDIR /dotnetapp

COPY RelistenApi/RelistenApi.csproj .

RUN dotnet restore

COPY RelistenApi/ .

RUN dotnet publish /p:Configuration=Release

RUN test -f bin/netcoreapp3.1/publish/RelistenApi.dll

ADD nginx.conf.sigil .

EXPOSE 3823

ENTRYPOINT ["dotnet", "bin/netcoreapp3.1/publish/RelistenApi.dll"]
