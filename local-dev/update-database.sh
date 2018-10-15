#!/usr/bin/env bash

TMPFILE="$(mktemp -t relisten.sql)"
URL="https://s3.us-east-2.amazonaws.com/relistenapi-db/relistenapi-db/postgres-relisten-db-2018-10-15-10-00-49.tgz"

curl "$URL" > "$TMPFILE"

rm $TMPFILE
