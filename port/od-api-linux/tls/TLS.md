# TLS termination for od-api-linux (TEST-ONLY)

Fronts the LAN/demonstrable case from the research (internet-facing tier
requires https + security certificates; LAN may use http) with a disposable
https proxy. All synthetic traffic; no production data.

## What was deployed

- Reverse proxy: **Caddy 2.11.7** (`caddy:2-alpine`), picked over nginx+openssl
  because it is fewer moving parts (no manual cert generation step).
- Container **`od-tls-proxy`** on network **`odnet`**, proxying
  `https://od-tls-proxy/*` → `http://od-api:8080/*` (paths, query strings, and
  the `Authorization` header preserved; Caddy forwards headers by default and
  the `header_up Authorization` line makes the ODFHIR contract explicit).
- **No published ports** (`docker port od-tls-proxy` returns empty; `80/tcp,
  443/tcp, 2019/tcp` in `docker ps` are image-EXPOSEd only, host mapping none).
- Files in this dir: `Caddyfile`, `docker-compose.yml` (equivalent deploy
  path), `.gitignore` (key/cert patterns). Remote copy of the Caddyfile lives
  at `~/od-linux/tls/Caddyfile` (md5 `3208e9edee86a0ba49d8e39fa5c0e370`,
  matches repo file); key material lives only in Docker volumes
  `od-tls-data` / `od-tls-config` on the remote host — never in the repo.
- Deploy command used (equivalent to `docker-compose.yml`):
  `docker run -d --name od-tls-proxy --network odnet
  -v ~/od-linux/tls/Caddyfile:/etc/caddy/Caddyfile:ro
  -v od-tls-data:/data -v od-tls-config:/config
  caddy:2-alpine caddy run --config /etc/caddy/Caddyfile --adapter caddyfile`

## Cert handling (explicitly TEST-ONLY)

- `tls internal`: Caddy minted a private internal CA + leaf cert at first
  start (`tls.obtain ... certificate obtained successfully ... issuer: local`).
  Clients must use `curl -k` or explicitly trust the internal root.
- Keys generated at deploy time inside the container/volume (`/data/caddy` in
  `od-tls-data`); **no private key material was committed** (see `.gitignore`).
- Gotcha hit during deploy: a bare `:443` catch-all site has no name to mint
  for, so the handshake died with `tlsv1 alert internal error`. Fix: the site
  block is named `od-tls-proxy` so SNI matches the container name clients use.
  The nginx+openssl fallback was NOT needed.

## Verification (from inside odnet; disposable sdk container, `curl -k`)

`docker run --rm --network odnet mcr.microsoft.com/dotnet/sdk:8.0 curl ...`

| # | Command (against `https://od-tls-proxy`) | Result |
|---|---|---|
| 1 | `curl -sk https://od-tls-proxy/health` | `200 {"status":"ok"}` |
| 2 | `curl -sk -H "Authorization: ODFHIR POCDEV/POC-CUST-KEY-1" "https://od-tls-proxy/api/v1/preferences?PrefName=DateLastAging"` | `200 {"PrefName":"DateLastAging","ValueString":"2026-10-01"}` (auth + query string through) |
| 3 | same URL, no `Authorization` | `401 {"ErrorMessage":"Malformed Authorization header. ..."}` |
| 4a | `-H "Authorization: ODFHIR POCDEV/POC-CUST-KEY-1" https://od-tls-proxy/api/v1/patients/1` | `200` spec-shaped patient (`PatNum:1 ... Birthdate:1986-11-06`) |
| 4b | `-H "Authorization: ODFHIR NOPE/NOPE" .../preferences?PrefName=DateLastAging` | `401 {"ErrorMessage":"Unknown developer/customer key pair."}` — backend evaluated the presented credential, so the header provably reaches it (200-vs-401) |
| 5 | `-H "Authorization: ODFHIR ..." "https://od-tls-proxy/api/v1/appointments?limit=2&offset=0&patNum=1"` | `200` JSON array (multi-param query preserved) |

## Cipher / TLS version observed (`curl -skv`)

- `SSL connection using TLSv1.3 / TLS_AES_128_GCM_SHA256`, `ALPN: server accepted h2`
- `issuer: CN=Caddy Local Authority - ECC Intermediate` (internal CA — expected)
- `SSL certificate verify result: unable to get local issuer certificate (20),
  continuing anyway` (expected under `-k`; must NOT happen in production)

## Production cutover needs (NOT done here)

1. Real trust chain: replace `tls internal` with ACME (public DNS) or a mounted
   CA-signed cert; clients must verify without `-k`.
2. HSTS (`Strict-Transport-Security`), TLS minimum version + explicit cipher
   policy (e.g. TLS ≥ 1.2, AEAD-only), and HTTP→HTTPS redirect policy review.
3. Key rotation + storage: private keys in a secret store/volume with rotation
   runbook, not an anonymous Docker volume; separate backup/retention rules.
4. Log handling: Caddy's default access log already redacts the
   `Authorization` header (`"Authorization": ["REDACTED"]` observed in
   `docker logs od-tls-proxy`), but there is still no retention/central-sink
   policy — add one and confirm no other secret-bearing headers are logged.
5. Out of scope but blocking real traffic anyway: HQ credential validation is
   still `STUBBED` in Program.cs, and `/__test/sink` must never be exposed
   beyond test networks.

## Deliberately did NOT do

- Did not stop/remove/rebuild/restart `od-api`, change its env, or publish
  host ports (verified: `od-api` up since 17:50 UTC, `docker port od-tls-proxy`
  empty). Only created `od-tls-proxy` (+ disposable `--rm` test containers).
- Did not commit key/cert material, touch `Program.cs`/`Shaping.cs`/
  `EventPoller.cs`/Dockerfiles/`seed.sql`/`VerifyApi/`/`README.md`/`docs/`/`db/`.
- Did not add HSTS/ACME/cipher policy (would imply production trust this
  setup does not have). No nginx fallback container was created.
