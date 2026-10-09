-- Housekeeping (§9.4) deletes published outbox rows and old inbox rows by age; these keep those scans cheap.
CREATE INDEX ix_outbox_published ON messaging.outbox (published_at) WHERE published_at IS NOT NULL;
CREATE INDEX ix_inbox_processed ON messaging.inbox (processed_at);
