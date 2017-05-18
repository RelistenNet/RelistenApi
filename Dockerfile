FROM microsoft/dotnet:1.1-sdk

WORKDIR /dotnetapp

COPY RelistenApi/RelistenApi.csproj .

RUN dotnet restore

COPY RelistenApi/ .

RUN dotnet publish /p:OutDir=out;Configuration=Release

RUN test -f out/RelistenApi.dll

EXPOSE 3823/tcp

ENTRYPOINT ["dotnet", "out/RelistenApi.dll"]
