#!/usr/bin/env bash
# Scripted check for od-mt-spike (replaces the manual curl from the README).
# Asserts: /health 200 + ok, ?wsdl 200 + contract, one SOAP round-trip
# returning ProcessRequestResult with the stub echo payload.
# Every assertion gates (no -k, no || true): any failure exits nonzero.
# Usage: bash port/od-mt-spike/check.sh [baseUrl]   (default http://127.0.0.1:8080)
set -euo pipefail

BASE="${1:-http://127.0.0.1:8080}"
BASE="${BASE%/}"

echo "== od-mt-spike check against $BASE =="

echo "-- [1/3] GET /health"
health="$(curl -fsS --max-time 10 "$BASE/health")"
echo "$health"
echo "$health" | grep -q '"status"[[:space:]]*:[[:space:]]*"ok"' || { echo "FAIL: /health body missing status ok"; exit 1; }
echo "PASS health"

echo "-- [2/3] GET /ServiceMain.asmx?wsdl"
wsdl="$(curl -fsS --max-time 10 "$BASE/ServiceMain.asmx?wsdl")"
echo "$wsdl" | grep -q "ProcessRequest" || { echo "FAIL: ?wsdl missing ProcessRequest contract"; exit 1; }
echo "$wsdl" | grep -q "ProcessRequestResult" || { echo "FAIL: ?wsdl missing ProcessRequestResult"; exit 1; }
echo "PASS wsdl"

echo "-- [3/3] POST SOAP ProcessRequest (DtoGetString round-trip)"
# dtoString param carries the DTO XML with one level of XML escaping
# (matches what SoapHttpClientProtocol puts on the wire).
dto='&lt;DtoGetString&gt;&lt;MethodName&gt;Spike.CiEcho&lt;/MethodName&gt;&lt;Credentials&gt;&lt;Username&gt;ci-check&lt;/Username&gt;&lt;/Credentials&gt;&lt;/DtoGetString&gt;'
req_file="$(mktemp)"
trap 'rm -f "$req_file"' EXIT
cat > "$req_file" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <soap:Body>
    <ProcessRequest xmlns="http://www.open-dent.com/OpenDentalServer">
      <dtoString>${dto}</dtoString>
    </ProcessRequest>
  </soap:Body>
</soap:Envelope>
EOF
resp="$(curl -fsS --max-time 20 -X POST "$BASE/ServiceMain.asmx" \
  -H "Content-Type: text/xml; charset=utf-8" \
  -H "SOAPAction: http://www.open-dent.com/OpenDentalServer/ProcessRequest" \
  --data @"$req_file")"
echo "$resp"
echo "$resp" | grep -q "ProcessRequestResult" || { echo "FAIL: SOAP response missing ProcessRequestResult"; exit 1; }
echo "$resp" | grep -Fq "spike:Method=Spike.CiEcho User=ci-check" || { echo "FAIL: SOAP round-trip echo mismatch"; exit 1; }
echo "PASS soap-round-trip"

echo "ALL CHECKS PASSED (3/3)"
