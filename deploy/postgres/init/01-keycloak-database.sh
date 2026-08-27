#!/bin/bash
# Creates the Keycloak database alongside the domain database.
#
# The postgres image creates POSTGRES_DB (talent) itself; Keycloak needs its own. Both live in one
# instance so the stack stays a single container for persistence — A1 reuses this same instance
# with pgvector added.
#
# Runs only on first initialisation of an empty data directory. If you change it, recreate the
# volume: `docker compose -f deploy/compose.yaml down -v`.

set -euo pipefail

KEYCLOAK_DB="${KEYCLOAK_DB:-keycloak}"

# CREATE DATABASE cannot run inside a transaction block, hence --single-transaction is absent.
# gexec is used so the create is a no-op when the database already exists, which keeps this
# script idempotent if it is ever re-run by hand.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
	SELECT format('CREATE DATABASE %I OWNER %I', '${KEYCLOAK_DB}', '${POSTGRES_USER}')
	WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '${KEYCLOAK_DB}')
	\gexec
EOSQL

echo "init: database '${KEYCLOAK_DB}' ready (owner '${POSTGRES_USER}')"
