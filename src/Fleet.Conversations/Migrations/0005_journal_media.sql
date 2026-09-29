-- 0005_journal_media.sql — the conversation journal, slice 4: archived media bytes (#388).
--
-- Additive and forward-only. Applied by `conversations migrate` on the DDL account, never at
-- startup, and applied whether or not media is enabled.
--
-- One row per object a subject has proven bytes for. `journal_objects` is the ONLY place an object
-- key exists, and the key never leaves the Comms process: an agent sees an upload id and nothing
-- else.
--
-- `owner` is the token subject that created the row. It binds who may COMPLETE and COMMIT an
-- upload — nothing more. Read access is authorised by message observership, never by owner, and
-- nothing here indexes or constrains reads.
--
-- `committed_sha256` is NULL until the object is committed and non-NULL afterwards. The unique key
-- on it is what makes dedup structural rather than a read-then-write race: at most one committed
-- object per digest can exist, in any transaction, at any isolation level.
--
-- No stored programs: the runner splits on `;` and does not support DELIMITER.

CREATE TABLE journal_objects (
  id               CHAR(26)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  object_key       VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  owner            VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  sha256           CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  committed_sha256 CHAR(64)     CHARACTER SET ascii COLLATE ascii_bin NULL,
  byte_size        BIGINT NOT NULL,
  mime_type        VARCHAR(127) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  state            ENUM('uploading','uploaded','committed','aborted','deleting') NOT NULL,
  created_at       DATETIME(6) NOT NULL,
  updated_at       DATETIME(6) NOT NULL,
  delete_after     DATETIME(6) NULL,
  UNIQUE KEY uq_object_key (object_key),
  UNIQUE KEY uq_committed_sha (committed_sha256),
  KEY ix_state_created (state, created_at),
  KEY ix_owner_state (owner, state)
) ENGINE=InnoDB;

-- The reference from a journal attachment to a committed object.
--
-- Added here rather than in 0004 because this is the slice that can fill it. The column and the
-- `committed` state existed since 0004 precisely so this slice stays additive: no table is rebuilt
-- and no existing row changes.
ALTER TABLE journal_attachments
  ADD CONSTRAINT fk_ja_object FOREIGN KEY (object_id) REFERENCES journal_objects(id);
