# Local Development

For local development you need redis and postgres. To make things easier there is a `docker-compose.yml` with a script wrapper to preload the database with information.

## Pre-requistes

1. Docker
2. Visual Studio Code (with C# extension)
3. .NET Core SDK 2.1+ (macOS: `brew cask install dotnet-sdk`)

## Database Setup

Run `./start-local-databases.sh`. This will check if you have the latest development backup of the database. This won't change all the time be prepared to have your database blown away and recreated when running this command in the future.

If you don't have the latest version, it will download a database backup and restore it to your docker container. This docker container is persisted to `local-dev/postgers-data/pgdata` so it will persist between docker launches.

In the future, if you would like to start the databases without checking for a new database backup, you can run:

```
docker-compose -f local-dev/docker-compose.yml up -d # the -d is optional to send the command to background
```

Both commands will start an Adminer server at [http://localhost:18080](http://localhost:18080) so that you can view the tables in the Postgres database. Here are the various connection infos to use Adminer or any other GUI tool (all hosts are `127.0.0.1` or `localhost`):

|  service  | port  | database name | username | password           | url |
| ----------- | ----- | ------------- | -------- | ------------------ | --- | 
| redis     | 16379 |               | -        | -                  | - |
| postgres  | 15432 | relisten_db   | relisten | local_dev_password | - |
| adminer   | 18080 | relisten_db   | relisten | local_dev_password | [http://localhost:18080](http://localhost:18080) |

## Code Setup

1. Open this repo folder in Visual Studio Code
2. If prompted, restore the packages for the project
3. Debug > Start Debugging (F5) or Debug > Start without Debugging (shift+F5)

Open the catalog API server at: [http://localhost:3823/api-docs](http://localhost:3823/api-docs)

## User Library API

The user-library API is a separate ASP.NET Core project and runs as a separate process from the catalog API:

```
ASPNETCORE_ENVIRONMENT=Development \
UserAuth__AccessTokenSigningKey=local-dev-signing-key-at-least-32-bytes-long \
dotnet run --project RelistenUserApi/RelistenUserApi.csproj --urls http://localhost:5119
```

Useful local checks:

```
curl -i http://localhost:5119/health
curl -i http://localhost:5119/api/v3/library/users/me
```

For local mobile simulator development, `Development` and `Test` environments expose:

```
POST http://localhost:5119/api/v3/library/auth/development/session
```

This endpoint is closed outside `Development`/`Test`.

Provider sign-in uses Apple/Google OIDC ID tokens:

```
POST http://localhost:5119/api/v3/library/auth/callback/google
POST http://localhost:5119/api/v3/library/auth/callback/apple
POST http://localhost:5119/api/v3/library/auth/reauthenticate/google
POST http://localhost:5119/api/v3/library/auth/reauthenticate/apple
```

Production provider auth fails closed until valid audiences are configured. Use environment variables or deployment secrets, not checked-in client files:

```
UserAuth__AccessTokenSigningKey=...
UserAuth__Google__Audiences__0=<google-web-or-mobile-client-id>
UserAuth__Apple__Audiences__0=<apple-service-id-or-bundle-id>
```

The provider callback request body sends an ID token as `provider_token` and the client nonce as `nonce`. Account export and deletion require recent provider reauthentication.

The user-library image is built separately from the catalog API:

```
docker build -f Dockerfile.userapi -t relisten-user-api:local .
docker run --rm -p 5119:5119 -e UserData__InitializeSchema=false relisten-user-api:local
```

That container command is a health-check smoke only. For a database-backed local container, pass `DATABASE_URL` and `UserAuth__AccessTokenSigningKey`.

Manual deployment uses the separate GitHub Actions workflow:

```
./deploy-user-api
```
