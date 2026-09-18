-- Widen submissions.external_ref to the identifier bound the server PUBLISHES.
--
-- `GET /v1/session` returns `identifierMaxLength: 128`, and the submissions route validates the
-- client's `submissionId` against exactly that. The column was CHAR(32) — sized for the 32-hex-char
-- identifier the tests happen to use — so every id between 33 and 128 characters passed validation
-- and then failed at the database.
--
-- Both of that mismatch's outcomes are wrong, and neither is visible from the route:
--
--   * under a strict SQL mode the insert errors, and the client gets `503` — which tells it to
--     retry something that can never succeed, for a value the server told it was legal;
--   * under a non-strict mode MySQL TRUNCATES to 32 characters, so two submissions whose ids differ
--     only after the 32nd character collide on ux_sub_extref. One silently replaces the other, and
--     the client that sent the second is told its submission was accepted.
--
-- Widening rather than lowering the validator, because the bound is already on the wire: a deployed
-- client has been told 128, and shrinking it server-side would start refusing identifiers that
-- `GET /v1/session` still advertises.
--
-- VARCHAR, not CHAR: CHAR(128) pads every stored value to 128 bytes and then strips trailing spaces
-- on read, which quietly makes "abc" and "abc   " the same identifier. ascii_bin keeps the
-- comparison byte-exact and case-sensitive, matching the old column and the validator's charset.
--
-- Index key length is unchanged in kind: conversation_id CHAR(26) plus external_ref VARCHAR(128),
-- both ascii, is far inside InnoDB's 3072-byte index limit.

ALTER TABLE submissions
  MODIFY external_ref VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL;
