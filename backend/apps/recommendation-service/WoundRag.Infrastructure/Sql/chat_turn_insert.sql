-- One turn. If another question already took this number, nothing is written and the caller answers 409.

INSERT INTO rag.chat_turns (chat_session_id, turn_number, question, removed, language, outcome, english, shown,
                            shown_language, message, notices, answered_by, problem, prompt_hash, created_at)
VALUES (@sessionId, @number, @question, @removed, @language, @outcome, @english::jsonb, @shown::jsonb,
        @shownLanguage, @message, @notices::jsonb, @answeredBy, @problem, @promptHash, @createdAt)
ON CONFLICT (chat_session_id, turn_number) DO NOTHING
RETURNING turn_number;
