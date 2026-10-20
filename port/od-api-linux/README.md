# od-api-linux — Linux-native Open Dental REST API host (vertical slice)

A .NET 8 (Kestrel) host serving a slice of the vendor REST contract (`/api/v1`) natively on
Linux, backed by MariaDB. Clean-room host + dispatch over the GPL schema; no original binaries.
Proves the API-services seam from the research (ODApi dispatch blob is 8 types; 117 `*ForApi`
handlers are GPL) with a pattern each further resource can copy.

**Status: 13/13 smoke checks pass on fleet-r02** (Debian 13, MariaDB 10.11 vendor-configured):
10 endpoint/auth checks + 3 events checks (delivered-once, `Event-Type`/auth headers,
no-redelivery proving `DateTimeStart` advancement).

## Contract (vs vendor apispec/apievents docs)

| Endpoint | Behavior |
|---|---|
| `GET /health` | `{"status":"ok"}`. Extension for container checks — NOT part of the OD contract. |
| `GET /api/v1/preferences?PrefName=X` | `{PrefName, ValueString}` / 404 `{ErrorMessage}`. |
| `GET /api/v1/patients/{patNum}` | Spec-shaped patient (enum strings: `Male`/`Patient`/`Married`; `Birthdate: yyyy-MM-dd`) / 404. |
| `GET /api/v1/appointments?limit=&offset=&patNum=` | Array, `limit` default 100 / max 1000 (vendor batch cap), `[]` when empty. |
| `GET /api/v1/payments?limit=&offset=&patNum=` | Array of `{PayNum, PatNum, PayDate (yyyy-MM-dd), PayAmt, CheckNum, BankBranch, PayType, ClinicNum, DateTStamp}` (`DateTStamp` = `SecDateTEdit`; payment has no `DateTStamp` column), `[]` when empty. |
| `GET /api/v1/payments/{payNum}` | Single payment, same shape / 404 `{ErrorMessage}`. |
| Failures | `{ErrorMessage}` with 400/401/404/503 — per spec ("a failure should return an explanation"). |

Auth: `Authorization: ODFHIR {DevName}/{CustApiKey}` checked against the `apikey` table (real).
**HQ credential validation (required by OD, cached 6h) is STUBBED** (`HQ_VALIDATION=stub` logs and
records; `=enforce` refuses with 503). A production port must implement the HQ call before serving
traffic — see research doc part 3. Enum text covers values used by the seed; unknown values fall
back to their numeric form (documented delta, not silent).

## Events poller (`EventPoller` BackgroundService)

Implements the vendor Database-Events protocol: per-subscription `PollingSeconds`, 1000-row batches,
3-day backfill clamp, `DateTimeStart` advances only on 2xx delivery, expired subscriptions skipped.
WatchTables in this slice: `Appointment`, `Patient` (table name whitelisted, never interpolated raw).
Outbound webhook carries `Authorization: <CustomerKey>`, `Workstation:`, `Event-Type: WatchTable: X`.
`POST /__test/sink` + `GET /__test/sink` are test-only subscriber stand-ins (no auth by design, capped
at 100 records) — never expose beyond test networks.

## Run (remote pattern; all disposable, odnet-internal, no published ports)

```bash
# schema + seed (disposable DB): patient/appointment/apikey skeema + seed.sql (synthetic only)
# build + run
docker build -t od-api-linux .
docker run -d --name od-api --network odnet -e OD_DB_HOST=od-mariadb -e OD_DB_USER=root -e OD_DB_PASS=root od-api-linux
# verify (10 checks: health, 3x auth failures, pref 200/404, patient 200/404, appts shape/empty)
docker run --rm --network odnet -v $PWD:/work mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c "cd /work/VerifyApi && dotnet run -- http://od-api:8080"
```

## Adding a resource (the repeatable pattern)

1. Confirm the `*ForApi` handler shape in `OpenDentBusiness/Data Interface/*` (117 exist) and the
   vendor spec page for the resource (field names, enum strings).
2. Add a `MapGet` (parameterized MySqlConnector query — never string-concatenated inputs),
   shape rows into spec-named JSON, reuse `Authorize` + `Err`.
3. Extend `seed.sql` (synthetic rows) + `VerifyApi` (200 shape + 404/empty cases).
4. Resources needing HQ-only data (Remote-mode routing, FHIR server handlers — not in GPL tree)
   stay out of scope; note them in the endpoint's comment.

## Limits of this slice

Subset only (3 of ~120 resources); single-office direct-DB (no middle-tier client mode); no TLS
termination here (terminate at reverse proxy); no API Events poller (spec recorded in research
doc part 6 — natural next slice); `ConvertZeroDateTime=true` tolerates legacy zero-dates.
