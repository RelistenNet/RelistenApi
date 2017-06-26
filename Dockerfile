FROM microsoft/dotnet:1.1-sdk

WORKDIR /dotnetapp

COPY RelistenApi/RelistenApi.csproj .

RUN dotnet restore

COPY RelistenApi/ .

RUN dotnet publish /p:Configuration=Release

RUN test -f bin/netcoreapp1.1/publish/RelistenApi.dll

EXPOSE $PORT/tcp

ENTRYPOINT ["dotnet", "bin/netcoreapp1.1/publish/RelistenApi.dll"]
