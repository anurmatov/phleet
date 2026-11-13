-- A conversation is with ONE agent (#530).
--
-- Until now a deployment had a single chat agent, named in configuration, and every command went to
-- it. A conversation now records its agent: that name is the routing key its commands are published
-- under and the subject an agent's token must carry to act on it.
--
-- ascii_bin, because the name is a routing key and a queue-name segment: it is compared byte for
-- byte by the broker, so it must be compared byte for byte here. The default collation would treat
-- 'Builder' and 'builder' as one agent.
--
-- Identity changes with it. It was (channel_id, external_ref), with the owner checked afterwards;
-- it is now (channel_id, principal_id, agent_name, external_ref), so two people — or one person
-- with two agents — can each have a conversation under the same client-chosen reference.
--
-- Rows that exist before this migration get the empty name. Nothing can route them and no client
-- can open them again (a new conversation always names its agent); they are left to the existing
-- retention rather than guessed into belonging to some agent.

ALTER TABLE conversations
  ADD COLUMN agent_name VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT '' AFTER principal_id,
  DROP INDEX ux_conv_channel_ref,
  ADD UNIQUE KEY ux_conv_identity (channel_id, principal_id, agent_name, external_ref);

-- Commands still pending for those rows have no queue to go to. They are retired rather than left
-- pending, where they would hold the oldest-pending-age gauge up for as long as the row exists.
UPDATE command_outbox o
  JOIN conversations c ON c.id = o.conversation_id
   SET o.state = 'failed'
 WHERE o.state = 'pending' AND c.agent_name = '';
