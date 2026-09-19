-- 0003_attachments.sql — inbound image attachments for the conversation protocol (#308 D3).
--
-- Metadata here; BYTES ON A VOLUME. That split is the decision, not an implementation detail: this
-- database is dumped nightly by the backup chain and validated by CREATE TABLE count, and 8 MiB
-- BLOBs would grow that dump by orders of magnitude and change the restore-time characteristics of
-- the one thing most worth restoring. Keeping the rows small keeps the transcript restorable.
--
-- Consequence, stated rather than discovered: attachment bytes are NOT in the database dump. Their
-- durability is the volume's. A restore of the database alone yields a transcript whose images serve
-- `410 attachment_gone`, which the client already renders as a permanent placeholder.
--
-- The two MySQL rules from 0001 apply unchanged: a column DEFAULT may use only CURRENT_TIMESTAMP as
-- a BARE function, so UTC_TIMESTAMP(6) must be PARENTHESISED.

CREATE TABLE conversation_attachments (
  id                 CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  conversation_id    CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,

  -- reserved -> sealed -> bound is the whole happy path; failed is terminal and never counted.
  --
  -- `bound` is load-bearing rather than cosmetic. The binding UPDATE is conditional on
  -- (state='sealed' AND submission_id IS NULL), so exactly ONE submission can ever reference a row.
  -- Without that, two live transcript entries could share one row and one retention clock, and
  -- pruning the older entry would delete the bytes under the newer one.
  state              ENUM('reserved','sealed','bound','failed') NOT NULL DEFAULT 'reserved',

  kind               ENUM('image','document','audio','video','other') NOT NULL DEFAULT 'image',

  -- The SNIFFED type, written at seal. The declared one is checked against the accepted set at
  -- reserve and then discarded: serving a client-declared type is how an uploaded HTML file becomes
  -- stored XSS (MUST NOT 8).
  content_type       VARCHAR(64) NOT NULL,

  -- What the per-conversation and per-deployment caps count for a row that has no bytes yet.
  -- Counting only sealed rows would make both caps unenforceable by construction.
  declared_byte_size BIGINT UNSIGNED NOT NULL,

  -- Actual bytes on disk, written at seal. Zero until then.
  byte_size          BIGINT UNSIGNED NOT NULL DEFAULT 0,

  -- The reserved digest, re-verified against what actually arrived.
  sha256             CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,

  -- Client-supplied label. Stored and echoed; NEVER used to build a path. The file lives at a path
  -- derived from `id`, so a filename carrying `../` reaches nothing.
  file_name          VARCHAR(255) NULL,

  -- SHA-256 of the upload capability, never the capability (#308 D6). A fast digest and not the
  -- Argon2id the auth store uses, deliberately: this is a 256-bit uniform random value where a KDF
  -- buys nothing, and it guards the ONE route that accepts megabytes — a 19 MiB-per-call KDF in
  -- front of an upload turns it into an amplifier.
  upload_token_sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,

  -- Both NULL until binding; stamped together, in one statement, inside the accept transaction.
  -- event_seq is the accept floor — the same seq the submission.text entry takes.
  submission_id      CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
  event_seq          BIGINT UNSIGNED NULL,

  created_at         DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  sealed_at          DATETIME(6) NULL,

  PRIMARY KEY (id),

  -- The cap sum, taken under the conversation row lock.
  KEY ix_attachment_conv_state (conversation_id, state),

  -- The two sweeps. Separate indexes because the two windows have DIFFERENT ANCHORS: the upload
  -- window runs from created_at and the submit window from sealed_at, and sharing one anchor would
  -- leave a client that sealed at minute 14 exactly one minute to send its message.
  KEY ix_attachment_upload_sweep (state, created_at),
  KEY ix_attachment_submit_sweep (state, sealed_at),

  -- Rebuilding a transcript payload, and the retention prune that follows the event.
  KEY ix_attachment_submission (submission_id),

  CONSTRAINT fk_attachment_conv FOREIGN KEY (conversation_id) REFERENCES conversations (id)
) ENGINE=InnoDB;
