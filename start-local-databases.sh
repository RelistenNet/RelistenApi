#!/usr/bin/env bash

realpath() {
    [[ $1 = /* ]] && echo "$1" || echo "$PWD/${1#./}"
}

cd "$(dirname "$(realpath "$0")")"

cd local-dev

S3_BUCKET="relistenapi-db"
S3_FOLDER=""

object_list=$(curl -s "https://$S3_BUCKET.s3.amazonaws.com/$S3_FOLDER?list-type=2")

most_recent=$(echo "$object_list" | xmllint --xpath "//*[local-name()='Contents']" - | sort -r | head -n 1 | xmllint --xpath '//*[local-name()="Key"]/text()' -)

DB_VERSION="$most_recent"
DB_VERSION_FILE="db.version"

launch() {
    echo "Database is up to date... Launching redis, postgres and adminer."
    echo
    echo "Here is the connection info if you'd like to connect your own tools instead of using Adminer:"
    echo
    echo "             port    database name   username   password              url"
    echo "[redis]      16379                   -          -                     -"
    echo "[postgres]   15432   relisten_db     relisten   local_dev_password    -"
    echo "[adminer]    18080   relisten_db     relisten   local_dev_password    http://localhost:18080"
    echo
    echo "To launch the Relisten API server, open the git repo in Visual Studio Code and do Debug > Start Debugging (F5) or Debug > Start without Debugging (shift+F5)"
    echo "The API server will be available at: http://localhost:3823/api-docs"
    echo
    echo "Running this command:"
    echo "> docker-compose -f local-dev/docker-compose.yml up -d"
    echo
    echo "Stop the databases with:"
    echo "> docker-compose -f local-dev/docker-compose.yml down"
    echo "or"
    echo "> ./stop-local-databases.sh"
    docker-compose up -d
    echo "Databases running :) Happy development!"
}

if [ "$DB_VERSION" = "$(cat $DB_VERSION_FILE)" ]; then
    launch
    exit
fi

echo "[db-update] Ensuring database is running before updating: "
echo "> docker-compose up"
docker-compose up -d

TMPFILE="relisten.tgz"
TMPFILETAR="relisten.tar"
URL="https://s3.us-east-2.amazonaws.com/relistenapi-db/${DB_VERSION}"

echo "Downloading $URL to $TMPFILE"

curl --fail "$URL" > $TMPFILE

res=$?
if test "$res" != "0"; then
   echo "the curl command failed with: $res"
fi

gunzip "$TMPFILE"
tar xvf "$TMPFILETAR"

echo "Recreating databases...ignore errors about a relisten_db not existing. This could take 2-10 minutes depending on your machine."

DOCKER_DB_NAME="$(docker-compose ps -q relisten-db)"
docker exec -i "$DOCKER_DB_NAME" psql -U relisten -d postgres -c "DROP DATABASE relisten_db"
docker exec -i "$DOCKER_DB_NAME" psql -U relisten -d postgres -c "CREATE DATABASE relisten_db"
pv backup/export | docker exec -i "$DOCKER_DB_NAME" pg_restore --dbname=relisten_db --no-acl --no-owner -U relisten -d relisten_db -vv

#rm "$TMPFILETAR"
#rm -rf backup/

printf "%s" "${DB_VERSION}" > "$DB_VERSION_FILE"

launch
