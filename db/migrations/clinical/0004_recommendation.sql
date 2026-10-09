CREATE TABLE clinical.recommendation (
    recommendation_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    assessment_id     uuid NOT NULL,
    revision          integer NOT NULL,
    mode              text NOT NULL CHECK (mode IN ('generated', 'extractive')),
    corpus_version    text NOT NULL,
    payload           jsonb NOT NULL,                 -- full response, see contracts/rag-response
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_recommendation_revision UNIQUE (assessment_id, revision),
    CONSTRAINT fk_recommendation_assessment
        FOREIGN KEY (assessment_id, revision)
        REFERENCES clinical.wound_assessment (assessment_id, revision)
);
