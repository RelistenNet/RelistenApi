FROM microsoft/dotnet:1.1-sdk-msbuild

WORKDIR /dotnetapp

COPY RelistenApi/ .

RUN dotnet restore
RUN dotnet publish /p:OutDir=out;Configuration=Release

EXPOSE 3823/tcp

ENTRYPOINT ["dotnet", "out/RelistenApi.dll"]
