FROM microsoft/dotnet:1.1-sdk-msbuild

WORKDIR /dotnetapp
COPY RelistenApi/RelistenApi.csproj .

RUN dotnet restore

COPY RelistenApi/ .
RUN dotnet publish -c Release -o out

EXPOSE 3823/tcp

ENTRYPOINT ["dotnet", "out/RelistenApi.dll"]
