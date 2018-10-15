#!/usr/bin/env bash

cd "$(dirname "$(realpath "$0")")"

docker-compose -f local-dev/docker-compose.yml down
