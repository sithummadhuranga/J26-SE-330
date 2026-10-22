-- One audit event. Run once per row.

INSERT INTO rag.audit (request_id, occurred_at, event_type, provider, requested_model, returned_model, attempt, outcome,
                       input_tokens, output_tokens, latency_ms, details)
VALUES (@requestId, @at, @EventType, @Provider, @RequestedModel, @ReturnedModel, @Attempt, @Outcome,
        @InputTokens, @OutputTokens, @LatencyMs, @DetailsJson::jsonb);
