-- The ablation's shadow tables (ablation/0001): written only when a service runs with Ablation__Enabled=true.
GRANT USAGE ON SCHEMA ablation TO persister_svc, orchestrator_svc;
GRANT INSERT ON ablation.wound_assessment TO persister_svc;
GRANT INSERT ON ablation.recommendation TO orchestrator_svc;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA ablation TO persister_svc, orchestrator_svc;
