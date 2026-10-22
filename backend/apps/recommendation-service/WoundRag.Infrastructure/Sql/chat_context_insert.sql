-- What a later chat on this answer starts from (rag/0011).

INSERT INTO rag.chat_contexts (request_id, findings, observed_fields, withheld_fields)
VALUES (@requestId, @findings, @observed, @withheld);
