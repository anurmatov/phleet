-- 0004_journal.sql — the conversation journal, slice 1: text-only message records (#375).
--
-- Additive and forward-only. Applied by `conversations migrate` on the DDL account, never at
-- startup, and applied whether or not the journal is enabled.
--
-- Every id, key, fingerprint, hash, MIME type and platform file id is ascii/ascii_bin, matching
-- 0001, so a later foreign key onto any of them has a matching charset. Human-readable text keeps
-- the database default.
--
-- The enum values and nullable columns that later slices write (agent_tool, agent_copy, client_*,
-- copied, committed, lost, sha256, object_id) exist now so those slices stay additive. Nothing in
-- this slice writes them. The media slice adds its object table and the object_id foreign key in a
-- migration of its own.
--
-- No stored programs: the runner splits on `;` and does not support DELIMITER.

CREATE TABLE journal_conversations (
  id               CHAR(26)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  conversation_key VARCHAR(160) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  channel          ENUM('telegram') NOT NULL,
  chat_kind        ENUM('private','group','supergroup') NOT NULL,
  telegram_bot_id  BIGINT NULL,
  telegram_chat_id BIGINT NOT NULL,
  title            VARCHAR(256) NULL,
  created_at       DATETIME(6) NOT NULL,
  last_message_at  DATETIME(6) NOT NULL,
  UNIQUE KEY uq_conversation_key (conversation_key)
) ENGINE=InnoDB;

CREATE TABLE journal_messages (
  id                  CHAR(26)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  conversation_id     CHAR(26)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  source_key          VARCHAR(64)  CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  order_key           BIGINT NOT NULL,
  fingerprint         CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  direction           ENUM('inbound','outbound') NOT NULL,
  sender_kind         ENUM('human','agent') NOT NULL,
  sender_id           VARCHAR(128) NOT NULL,
  sender_display      VARCHAR(128) NULL,
  reply_to_source_key VARCHAR(64)  CHARACTER SET ascii COLLATE ascii_bin NULL,
  media_group_id      VARCHAR(64)  CHARACTER SET ascii COLLATE ascii_bin NULL,
  sent_at             DATETIME(6) NOT NULL,
  recorded_at         DATETIME(6) NOT NULL,
  text                MEDIUMTEXT NULL,
  text_format         ENUM('plain','html','rich') NULL,
  transcript          MEDIUMTEXT NULL,
  transcript_truncated BOOL NOT NULL DEFAULT 0,
  origin              ENUM('telegram_update','agent_runtime','agent_tool','agent_copy','client_submission','client_turn') NOT NULL,
  delivery_state      ENUM('received','sent','copied') NOT NULL,
  send_group_id       CHAR(26)     CHARACTER SET ascii COLLATE ascii_bin NULL,
  send_part           SMALLINT NULL,
  send_parts          SMALLINT NULL,
  UNIQUE KEY uq_source (conversation_id, source_key),
  KEY ix_order (conversation_id, order_key),
  KEY ix_sent (sent_at, id),
  KEY ix_media_group (conversation_id, media_group_id),
  FULLTEXT KEY ft_text (text, transcript),
  CONSTRAINT fk_jm_conv FOREIGN KEY (conversation_id) REFERENCES journal_conversations(id)
) ENGINE=InnoDB;

-- One row per runtime that saw the message. `fingerprint` is what THIS observer submitted, so a
-- reused event id can be cross-checked against it.
CREATE TABLE journal_message_observers (
  message_id  CHAR(26)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  observer    VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  event_id    CHAR(26)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  fingerprint CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  observed_at DATETIME(6) NOT NULL,
  PRIMARY KEY (message_id, observer),
  UNIQUE KEY uq_event (event_id),
  KEY ix_observer (observer, message_id),
  CONSTRAINT fk_jo_msg FOREIGN KEY (message_id) REFERENCES journal_messages(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE journal_attachments (
  id                      CHAR(26)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  message_id              CHAR(26)    CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ordinal                 TINYINT UNSIGNED NOT NULL,
  kind                    ENUM('photo','document','voice','video','video_note','audio','animation','sticker','other') NOT NULL,
  mime_type               VARCHAR(127) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  byte_size               BIGINT NULL,
  sha256                  CHAR(64)    CHARACTER SET ascii COLLATE ascii_bin NULL,
  original_file_name      VARCHAR(255) NULL,
  telegram_file_unique_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
  object_id               CHAR(26)    CHARACTER SET ascii COLLATE ascii_bin NULL,
  state                   ENUM('committed','not_archived','lost') NOT NULL,
  not_archived_reason     ENUM('media_disabled','over_bot_api_limit','over_size_cap','unsupported_kind','download_failed','source_expired','copied') NULL,
  created_at              DATETIME(6) NOT NULL,
  committed_at            DATETIME(6) NULL,
  UNIQUE KEY uq_ordinal (message_id, ordinal),
  CONSTRAINT fk_ja_msg FOREIGN KEY (message_id) REFERENCES journal_messages(id) ON DELETE CASCADE
) ENGINE=InnoDB;
