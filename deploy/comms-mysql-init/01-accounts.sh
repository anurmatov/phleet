#!/bin/sh
# Provision the two conversation-database accounts, on first start only.
#
# ⚠️ A SHELL SCRIPT RATHER THAN A .sql FILE, and the reason is the only thing that matters here:
# a .sql file cannot read an environment variable, so the passwords had to be literals in it — in a
# public repository, in every checkout, and in the image of every deployment that copied it. A
# placeholder is no better: an operator who does not notice it ships a database whose accounts have
# the password this file names, and one who does notice has to edit a tracked file to deploy.
#
# `docker-entrypoint.sh` runs anything executable in /docker-entrypoint-initdb.d, so the credentials
# come from the container's own environment and never from source control.
#
# ⚠️ THE SPLIT IS THE POINT. The service runs as `comms_runtime`, which holds
# SELECT/INSERT/UPDATE/DELETE and NO DDL grants at all. `conversations migrate` uses `comms_ddl`,
# which the running container is never given. That is what makes "the service never applies a
# migration at startup" a property of the DEPLOYMENT rather than of the code being careful: a
# process that tried would be refused by the database. Granting DDL to the runtime account turns a
# bug into a migration and removes the guard that would have caught it.
set -eu

: "${FLEET_COMMS_MYSQL_DDL_PASSWORD:?set FLEET_COMMS_MYSQL_DDL_PASSWORD on the comms-mysql service}"
: "${FLEET_COMMS_MYSQL_RUNTIME_PASSWORD:?set FLEET_COMMS_MYSQL_RUNTIME_PASSWORD on the comms-mysql service}"

database="${MYSQL_DATABASE:-comms}"

# Piped in rather than passed on the command line: an argument is visible in `ps` to anything else
# in the container for as long as the statement runs.
mysql --protocol=socket -uroot -p"${MYSQL_ROOT_PASSWORD}" <<SQL
CREATE DATABASE IF NOT EXISTS \`${database}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- The migration account. Schema changes only ever happen through this one, run by an operator.
CREATE USER IF NOT EXISTS 'comms_ddl'@'%'
  IDENTIFIED BY '${FLEET_COMMS_MYSQL_DDL_PASSWORD}';
GRANT ALL PRIVILEGES ON \`${database}\`.* TO 'comms_ddl'@'%';

-- The runtime account. Data only.
--
-- Deliberately NOT granted CREATE, ALTER, DROP, INDEX, REFERENCES or any other DDL privilege.
-- Adding one here is the mutation the design names: it makes the startup-never-migrates assertion
-- pass for the wrong reason.
CREATE USER IF NOT EXISTS 'comms_runtime'@'%'
  IDENTIFIED BY '${FLEET_COMMS_MYSQL_RUNTIME_PASSWORD}';
GRANT SELECT, INSERT, UPDATE, DELETE ON \`${database}\`.* TO 'comms_runtime'@'%';

FLUSH PRIVILEGES;
SQL
