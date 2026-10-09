-- INSERT ... ON CONFLICT (columns) needs SELECT on the conflict-target columns, which grants/0001 left out for two
-- writers: the persister's wound upsert and the orchestrator's recommendation insert (both ON CONFLICT DO NOTHING).
-- Column-level, so neither service can read anything more than the key it checks.
GRANT SELECT (wound_id) ON clinical.wound TO persister_svc;
GRANT SELECT (assessment_id, revision) ON clinical.recommendation TO orchestrator_svc;
