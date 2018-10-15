FROM microsoft/dotnet:2.1.403-sdk

WORKDIR /dotnetapp

COPY RelistenApi/RelistenApi.csproj .

RUN dotnet restore

COPY RelistenApi/ .

RUN dotnet publish /p:Configuration=Release

RUN test -f bin/netcoreapp2.1/publish/RelistenApi.dll

ADD nginx.conf.sigil .

EXPOSE 3823

ENTRYPOINT ["dotnet", "bin/netcoreapp2.1/publish/RelistenApi.dll"]
