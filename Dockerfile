FROM mcr.microsoft.com/dotnet/sdk:7.0

WORKDIR /dotnetapp

COPY RelistenApi/RelistenApi.csproj .

RUN dotnet restore

COPY RelistenApi/ .

RUN dotnet publish /p:Configuration=Release

RUN test -f bin/net7.0/publish/RelistenApi.dll

EXPOSE 3823

ENTRYPOINT ["dotnet", "bin/net7.0/publish/RelistenApi.dll"]
