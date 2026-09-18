-- 0001_initial_schema.sql — the durable conversation store (#276 §6).
--
-- MySQL 8.0, InnoDB, utf8mb4_0900_ai_ci.
--
-- ULID below is CHAR(26) CHARACTER SET ascii COLLATE ascii_bin: sortable, opaque, index-friendly,
-- and readable during incident triage.
--
-- ── Two MySQL rules that are different from each other and both easy to get wrong ──────
--
--   * A column DEFAULT may use only CURRENT_TIMESTAMP as a BARE function. UTC_TIMESTAMP(6) must be
--     PARENTHESISED: `DEFAULT (UTC_TIMESTAMP(6))`. Unparenthesised, this script fails at apply time
--     and every integration test dies together.
--   * ON UPDATE accepts only `CURRENT_TIMESTAMP(6)` — the parenthesised expression form is NOT
--     valid there.
--
-- Both forms appear below, each used where it is legal. They are not interchangeable.
--
-- DATETIME(6) everywhere. Never compare a written timestamp against a value at a different
-- precision: a sub-second/whole-second mismatch makes a conditional silently always-false, and a
-- green suite will not notice.

CREATE TABLE conversations (
  id                 CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  channel_id         VARCHAR(64)  NOT NULL,
  principal_id       VARCHAR(64)  NOT NULL,
  external_ref       VARCHAR(191) NOT NULL,
  next_seq           BIGINT UNSIGNED NOT NULL DEFAULT 1,
  retained_floor_seq BIGINT UNSIGNED NOT NULL DEFAULT 1,
  -- The append serialization point is this row's lock, not an in-process actor: one deployable is
  -- not one instance, and an actor would break silently the first time the deployment scaled to two.
  appender_epoch     CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  last_ordinal       BIGINT UNSIGNED NOT NULL DEFAULT 0,
  state              ENUM('open','closed') NOT NULL DEFAULT 'open',
  created_at         DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  last_activity_at   DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (id),
  UNIQUE KEY ux_conv_channel_ref (channel_id, external_ref),
  KEY ix_conv_principal (principal_id, last_activity_at)
) ENGINE=InnoDB;

CREATE TABLE submissions (
  id                  CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  conversation_id     CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  external_ref        CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  idempotency_key     VARCHAR(128) NULL,
  payload_fingerprint CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  state               ENUM('pending_dispatch','running','queued','merged','terminal')
                        NOT NULL DEFAULT 'pending_dispatch',
  disposition         ENUM('ran','injected','queued','queue_full','dropped') NULL,

  -- ⚠️ TWO SEQ COLUMNS WITH CONFUSINGLY SIMILAR NAMES. They are not the same value.
  --
  -- accepted_seq is #276 §6's: the seq OF THE submission.accepted EVENT. NULL until the
  -- disposition. It is internal, and `terminal_seq = accepted_seq` for queue_full and dropped
  -- refers to this one.
  accepted_seq        BIGINT UNSIGNED NULL,

  -- accept_floor_seq is the WIRE `acceptedSeq` (#298): the conversation's next_seq as read inside
  -- TX1a — the first seq any event about this submission can occupy. NOT NULL, because every
  -- response about this submission returns it: the first 201, an idempotent replay, and the 201 for
  -- a submission abandoned before its disposition. Returning accepted_seq here instead shows up as
  -- a null on every first accept.
  accept_floor_seq    BIGINT UNSIGNED NOT NULL,

  terminal_seq        BIGINT UNSIGNED NULL,
  created_at          DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (id),
  -- Dropping this index is how fifty concurrent submissions with one key become fifty rows.
  UNIQUE KEY ux_sub_idem (conversation_id, idempotency_key),
  UNIQUE KEY ux_sub_extref (conversation_id, external_ref),
  KEY ix_sub_live (conversation_id, state, created_at),
  CONSTRAINT fk_sub_conv FOREIGN KEY (conversation_id) REFERENCES conversations (id)
) ENGINE=InnoDB;

CREATE TABLE execution_attempts (
  id                  CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  submission_id       CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  host_attempt_id     CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  attempt             SMALLINT UNSIGNED NOT NULL,
  turn_id             VARCHAR(64) NULL,
  state               ENUM('pending','running','merged','committed','abandoned')
                        NOT NULL DEFAULT 'pending',
  -- This service between TX1a and the disposition; the AGENT from the disposition onward. Never
  -- NULL after TX1a, so the reconciler's predicate never reads one.
  lease_owner         VARCHAR(128) NULL,
  lease_expires_at    DATETIME(6) NULL,
  had_external_effect TINYINT(1) NOT NULL DEFAULT 0,
  started_at          DATETIME(6) NULL,
  committed_at        DATETIME(6) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_attempt (submission_id, attempt),
  KEY ix_attempt_lease (state, lease_expires_at),
  KEY ix_attempt_host (host_attempt_id),
  CONSTRAINT fk_attempt_sub FOREIGN KEY (submission_id) REFERENCES submissions (id)
) ENGINE=InnoDB;

CREATE TABLE conversation_events (
  conversation_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  seq             BIGINT UNSIGNED NOT NULL,
  event_id        CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  kind            VARCHAR(64) NOT NULL,
  submission_id   CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  attempt_id      CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  -- Only fields already on the protocol allowlist. The store adds no field to any payload, and a
  -- tool argument, a reasoning trace or a raw exception string never reaches a row: a database is a
  -- more durable place to leak into than a socket.
  payload_json    JSON NULL,
  emitted_at      DATETIME(6) NOT NULL,
  is_terminal     TINYINT(1) NOT NULL DEFAULT 0,
  retention_class ENUM('ephemeral','durable') NOT NULL DEFAULT 'durable',
  PRIMARY KEY (conversation_id, seq),
  UNIQUE KEY ux_event_id (event_id),
  KEY ix_event_gc (conversation_id, retention_class, seq),
  KEY ix_event_submission (submission_id, seq)
) ENGINE=InnoDB;

-- Two outboxes, not one table with a destination column.
--
-- They have different identities (submission versus eventId), different routing (one agent queue
-- versus an event exchange), different retention and different consumers. One table would make the
-- unique index and the retention rules ambiguous — and an ambiguous unique index on an at-least-once
-- path is how a duplicate turn gets created.

CREATE TABLE event_outbox (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  conversation_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  seq             BIGINT UNSIGNED NOT NULL,
  event_id        CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  state           ENUM('pending','published','failed') NOT NULL DEFAULT 'pending',
  attempts        SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  next_attempt_at DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  -- Written ONLY after the broker confirms. Marking it before the confirm is how an event is lost
  -- while the row says it was delivered.
  published_at    DATETIME(6) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_outbox_event (event_id),
  KEY ix_outbox_claim (state, next_attempt_at, id),
  KEY ix_outbox_gc (state, published_at)
) ENGINE=InnoDB;

CREATE TABLE command_outbox (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  -- NULL for a cancel, which has no submission of its own.
  submission_id   CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  conversation_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  -- The broker messageId. The delivery claim key is 'inbound:' + this.
  message_id      CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  kind            VARCHAR(64) NOT NULL,
  payload_json    JSON NOT NULL,
  state           ENUM('pending','published','failed') NOT NULL DEFAULT 'pending',
  attempts        SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  next_attempt_at DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  published_at    DATETIME(6) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_command_message (message_id),
  KEY ix_command_claim (state, next_attempt_at, id),
  KEY ix_command_gc (state, published_at),
  CONSTRAINT fk_command_conv FOREIGN KEY (conversation_id) REFERENCES conversations (id)
) ENGINE=InnoDB;

CREATE TABLE delivery_claims (
  claim_key   VARCHAR(191) NOT NULL,
  owner       VARCHAR(128) NOT NULL,
  state       ENUM('held','done') NOT NULL DEFAULT 'held',
  result_ref  CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  -- Set only when the claim is completed, so a retried south write can return the recorded result
  -- instead of writing a second time.
  disposition ENUM('ran','injected','queued','queue_full','dropped') NULL,
  accepted_seq BIGINT UNSIGNED NULL,
  attempt_ref CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  claimed_at  DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  expires_at  DATETIME(6) NOT NULL,
  PRIMARY KEY (claim_key),
  KEY ix_claim_expiry (state, expires_at),
  KEY ix_claim_gc (state, claimed_at)
) ENGINE=InnoDB;

CREATE TABLE client_cursors (
  client_instance_id VARCHAR(128) NOT NULL,
  conversation_id    CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  delivered_seq      BIGINT UNSIGNED NOT NULL DEFAULT 0,
  read_seq           BIGINT UNSIGNED NOT NULL DEFAULT 0,
  -- ON UPDATE takes CURRENT_TIMESTAMP(6), NOT the parenthesised (UTC_TIMESTAMP(6)) form used for
  -- DEFAULT above. The two rules are different; both are used correctly here.
  updated_at         DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6))
                       ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (client_instance_id, conversation_id),
  CONSTRAINT fk_cursor_conv FOREIGN KEY (conversation_id) REFERENCES conversations (id)
) ENGINE=InnoDB;

-- Single row. Drives the reconciler's grace period after service recovery, so a restart does not
-- abandon every in-flight attempt the instant it comes back.
CREATE TABLE service_health (
  id              TINYINT UNSIGNED NOT NULL DEFAULT 1,
  last_healthy_at DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (id)
) ENGINE=InnoDB;

INSERT INTO service_health (id, last_healthy_at) VALUES (1, UTC_TIMESTAMP(6));
