-- Additive resend credentials and operator-only copy provenance; no backfill.
ALTER TABLE journal_attachments
    ADD COLUMN telegram_file_id VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NULL,
    ADD COLUMN telegram_file_id_bot_id BIGINT NULL,
    ADD COLUMN copied_from_message_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
    ADD COLUMN copied_from_ordinal TINYINT UNSIGNED NULL;

-- The source is intentionally not a foreign key: retention may delete it independently.
ALTER TABLE journal_messages
    ADD KEY ix_participant (conversation_id, sender_kind, sender_id, sent_at);
