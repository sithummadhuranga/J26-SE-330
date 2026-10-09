#!/usr/bin/env bash
# Creates all Kafka topics; safe to re-run. Usage: bash infra/kafka/create-topics.sh [demo|later]
set -euo pipefail

TIER="${1:-demo}"
BOOTSTRAP="localhost:9092"   # inside the kafka container
RF=1                         # demo: single broker. Production: 3 with min.insync.replicas=2

if [ "$TIER" = "later" ]; then
  MAIN=12; READY=6
else
  MAIN=6; READY=3
fi

# Drop kafka-topics.sh's harmless '.' vs '_' warning but keep every other message.
create() {
  local topic="$1" partitions="$2"
  docker exec kafka /opt/kafka/bin/kafka-topics.sh \
    --bootstrap-server "$BOOTSTRAP" --create --if-not-exists \
    --topic "$topic" --partitions "$partitions" --replication-factor "$RF" 2>&1 \
    | { grep -v "Due to limitations in metric names" || true; }
}

create wound-events              "$MAIN"    # key: woundId
create wound-events.persisted    "$MAIN"    # key: woundId
create recommendations.ready     "$READY"   # key: assessmentId
create wound-events.retry.30s    3          # key: woundId
create wound-events.retry.5m     3          # key: woundId
create wound-events.dlq          1          # key: woundId

docker exec kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server "$BOOTSTRAP" --list
