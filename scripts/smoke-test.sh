#!/usr/bin/env bash
# End-to-end check against a running Docker Compose stack: upload a real PX4 log and wait until the worker has
# processed it. Usage: scripts/smoke-test.sh [base-url]   (needs curl and jq; self-registration must be enabled)
set -euo pipefail

BASE=${1:-http://127.0.0.1:8080}
LOG=${LOG_FILE:-tests/fixtures/px4/sample.ulg}
EMAIL="smoke-$(date +%s)-$RANDOM@flightlog.test"
PASSWORD="Smoke-Test-Password-1"

json() { curl --fail-with-body -sS -H 'content-type: application/json' "$@"; }

json -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\",\"displayName\":\"Smoke\"}" "$BASE/api/v1/auth/register" > /dev/null
TOKEN=$(json -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" "$BASE/api/v1/auth/login" | jq -r .accessToken)
AUTH=(-H "authorization: Bearer $TOKEN")

TEAM=$(json "${AUTH[@]}" -d '{"name":"Smoke team"}' "$BASE/api/v1/teams" | jq -r .id)
PROJECT=$(json "${AUTH[@]}" -d '{"name":"Smoke project"}' "$BASE/api/v1/teams/$TEAM/projects" | jq -r .id)
UPLOAD=$(curl --fail-with-body -sS "${AUTH[@]}" -F "file=@$LOG" "$BASE/api/v1/projects/$PROJECT/logs")
FILE=$(echo "$UPLOAD" | jq -r .logFile.id)
FLIGHT=$(echo "$UPLOAD" | jq -r .flight.id)
echo "Uploaded $LOG as log file $FILE"

for _ in $(seq 1 60); do
  STATUS=$(curl --fail-with-body -sS "${AUTH[@]}" "$BASE/api/v1/log-files/$FILE" | jq -r .status)
  case "$STATUS" in
    Processed) break ;;
    Failed|Cancelled)
      curl -sS "${AUTH[@]}" "$BASE/api/v1/log-files/$FILE/jobs" | jq .
      echo "Processing ended as $STATUS" >&2
      exit 1 ;;
  esac
  sleep 2
done

if [ "$STATUS" != "Processed" ]; then
  echo "Log was not processed in time (status: $STATUS)" >&2
  exit 1
fi

SIGNALS=$(curl --fail-with-body -sS "${AUTH[@]}" "$BASE/api/v1/flights/$FLIGHT/signals" | jq length)
POINTS=$(curl --fail-with-body -sS "${AUTH[@]}" "$BASE/api/v1/flights/$FLIGHT/signals/data?names=attitude.roll&maxPoints=500" | jq '.signals[0].values | length')
FIRMWARE=$(curl --fail-with-body -sS "${AUTH[@]}" "$BASE/api/v1/flights/$FLIGHT" | jq -r .firmwareVersion)
echo "Processed: firmware '$FIRMWARE', $SIGNALS signals, attitude.roll returned $POINTS points"
[ "$SIGNALS" -gt 0 ] && [ "$POINTS" -gt 0 ]
