-- Two accounts for the conversation database, provisioned on first start.
--
-- ⚠️ THE SPLIT IS THE POINT. The service runs as `comms_runtime`, which holds
-- SELECT/INSERT/UPDATE/DELETE and NO DDL grants at all. `conversations migrate` uses
-- `comms_ddl`, which the running container is never given.
--
-- That is what makes "the service never applies a migration at startup" a property of the
-- DEPLOYMENT rather than of the code being careful: a process that tried would be refused by
-- the database. Granting DDL to the runtime account turns a bug into a migration and removes
-- the guard that would have caught it.
--
-- Passwords come from the environment; there are no defaults here on purpose. A compose file
-- in a public repository is the last place a working credential should be able to come from.

CREATE DATABASE IF NOT EXISTS `comms` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- The migration account. Schema changes only ever happen through this one, by an operator.
CREATE USER IF NOT EXISTS 'comms_ddl'@'%'
  IDENTIFIED BY 'REPLACE_WITH_FLEET_COMMS_MYSQL_DDL_PASSWORD';
GRANT ALL PRIVILEGES ON `comms`.* TO 'comms_ddl'@'%';

-- The runtime account. Data only.
--
-- Deliberately NOT granted CREATE, ALTER, DROP, INDEX, REFERENCES or any other DDL privilege.
-- Adding one here is the mutation the design names: it makes the startup-never-migrates
-- assertion pass for the wrong reason.
CREATE USER IF NOT EXISTS 'comms_runtime'@'%'
  IDENTIFIED BY 'REPLACE_WITH_FLEET_COMMS_MYSQL_RUNTIME_PASSWORD';
GRANT SELECT, INSERT, UPDATE, DELETE ON `comms`.* TO 'comms_runtime'@'%';

FLUSH PRIVILEGES;
