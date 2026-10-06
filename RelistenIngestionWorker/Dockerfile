FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish RelistenIngestionWorker/RelistenIngestionWorker.csproj -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "RelistenIngestionWorker.dll"]
