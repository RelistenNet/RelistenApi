# Local Development

For local development you need redis and postgres. To make things easier there is a `docker-compose.yml` with a script wrapper to preload the database with information.

## Pre-requistes

1. Docker
2. Visual Studio Code (with C# extension)
3. .NET Core SDK 2.1+ (macOS: `brew cask install dotnet-sdk`)

## Database Setup

Install pv (`brew install pv`).

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

Open the API Server at: [http://localhost:3823/api-docs](http://localhost:3823/api-docs)
