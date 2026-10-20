# odstack — one-command disposable Linux lab (`port/stack/`)

`docker compose up --build` (run from this directory) brings up the full lab slice
on an isolated project (`odstack-*` containers, `odstack_odnet` network,
`odstack_*` volumes): MariaDB 10.11 (vendor-exact config, seeded once) →
od-api-linux → Caddy TLS front → Samba OpenDentImages share. **No published host
ports.** All verification runs from inside the compose network, exactly like the
live lab on `fleet-r02`.

TEST-ONLY throughout: synthetic seed data, `tls internal` self-signed certs,
root/root DB credential. Never production data, never real trust.

## Files in this directory (the only files this stack adds; nothing else touched)

| File | What it is |
|---|---|
| `docker-compose.yml` | The stack (5 services, 1 network, 4 volumes). |
| `od-my.cnf` | Committed copy of the vendor-exact MariaDB config. Source of truth is the remote `~/od-linux/od-my.cnf` (fetched `2026-10-20`, byte-identical — see verification below). Mounted read-only at `/etc/mysql/conf.d/od-my.cnf`. |
| `initdb/01-schema.sql` | Frozen copy of the 10-table skeema subset (`apikey preference patient appointment apisubscription payment paysplit signalod eservicesignal alertitem` from `db/skeema/`). Plus one added `USE opendental;` line (documented delta). |
| `initdb/02-seed.sql` | Byte-copy of `../od-api-linux/seed.sql` (synthetic only). Plus the same added `USE` line. |
| `STACK.md` | This file. |

Everything else is referenced, never modified: the API is built from the
untouched `../od-api-linux/Dockerfile`; TLS terminates with the untouched
`../od-api-linux/tls/Caddyfile` via a relative read-only volume.

## Up

```bash
cd port/stack
docker compose up --build -d
docker compose ps            # expect 5 containers: od-mariadb, od-api, od-tls-proxy, od-samba + one-shot images-perms (exited 0)
docker compose logs od-mariadb | grep -i 'ready for connections'
```

Ordering is handled by compose, not luck: `od-api` waits for `od-mariadb`
`service_healthy` (the healthcheck passes only after the server is up, which is
*after* `initdb/*.sql` have applied — the MariaDB entrypoint runs
`/docker-entrypoint-initdb.d` during first initialization of an empty datadir,
then starts accepting connections). Seed is therefore present before the first
API query. `od-tls-proxy` waits for `od-api` start; `od-samba` waits for the
`images-perms` one-shot (`chmod 777`, reproducing the lab's manual fix).

## Verify (all inside the stack network; `<p>` = your `docker compose` project)

```bash
P=odstack
# 1. config actually loaded by the server (not just mounted)
docker exec $P-od-mariadb-1 mariadb -uroot -proot -e \
  "SELECT @@default_storage_engine,@@max_allowed_packet,@@max_connections,@@sql_mode,@@explicit_defaults_for_timestamp,@@lower_case_table_names;"
# expect: MyISAM | 41943040 | 3000 | (empty) | 1 | 1

# 2. seed applied once: 10 tables, 1 row each in the seeded tables
docker exec $P-od-mariadb-1 mariadb -uroot -proot opendental -e \
  "SELECT (SELECT COUNT(*) FROM patient) pat,(SELECT COUNT(*) FROM appointment) apt,(SELECT COUNT(*) FROM apikey) k,(SELECT COUNT(*) FROM payment) pay,(SELECT COUNT(*) FROM paysplit) ps;"

# 3. health through the stack (direct + TLS front)
docker run --rm --network $P_odnet mcr.microsoft.com/dotnet/sdk:8.0 \
  curl -s http://od-api:8080/health
docker run --rm --network $P_odnet mcr.microsoft.com/dotnet/sdk:8.0 \
  curl -sk https://od-tls-proxy/health
# both: {"status":"ok"}

# 4. one authenticated API call through the TLS front (proves header + query + SNI path)
docker run --rm --network $P_odnet mcr.microsoft.com/dotnet/sdk:8.0 curl -sk \
  -H "Authorization: ODFHIR POCDEV/POC-CUST-KEY-1" \
  "https://od-tls-proxy/api/v1/preferences?PrefName=DateLastAging"
# expect 200 {"PrefName":"DateLastAging","ValueString":"2026-10-01"}

# 5. full 13-check API + 3-check events suite (needs the repo's VerifyApi; ~75s for the events leg)
docker run --rm --network $P_odnet -v <repo>/port/od-api-linux/VerifyApi:/work \
  mcr.microsoft.com/dotnet/sdk:8.0 bash -c "cd /work && dotnet run -- http://od-api:8080"

# 6. samba put/get (byte-identical) + case-insensitive get (Windows parity)
docker run --rm --network $P_odnet alpine sh -c \
  "apk add -q samba-client; echo synthetic-image-bytes > /tmp/XRAY_001.png; \
   smbclient //od-samba/OpenDentImages -U oduser%odpass -c \"put /tmp/XRAY_001.png XRAY_001.png; get XRAY_001.png /tmp/got.png\" && cmp /tmp/XRAY_001.png /tmp/got.png && echo ROUNDTRIP_OK; \
   smbclient //od-samba/OpenDentImages -U oduser%odpass -c \"get xray_001.png /tmp/got_lower.png\" && echo CASE_INSENSITIVE_GET_OK"
```

(`$P_odnet` because compose names the network `<project>_odnet`; with `name:
odstack` that is `odstack_odnet`.)

## Down (full teardown — leaves nothing behind)

```bash
cd port/stack
docker compose down -v        # stops containers AND deletes odstack_* volumes (DB, images, TLS keys)
docker compose ps -a          # expect: nothing
docker volume ls | grep odstack || echo "no odstack volumes remain"
docker network ls | grep odstack || echo "no odstack network remains"
```

## Deltas vs the lab pieces (all contained in this directory)

1. **Service names mirror lab hostnames** (`od-mariadb`, `od-api`, `od-tls-proxy`,
   `od-samba`) so the *unmodified* lab Caddyfile resolves (`od-tls-proxy` SNI site,
   `http://od-api:8080` upstream) on the isolated stack network. Isolation comes
   from the compose project (`odstack-*-1` container names, `odstack_odnet`
   network, `odstack_*` volumes) — DNS is per-network, so these names never
   resolve to, or collide with, the live lab containers on `odnet`.
2. **`initdb/01-schema.sql`**: concatenation of the 10 committed skeema files
   (verified byte-exact sans header) + one added `USE opendental;` line.
   Belt-and-braces: the entrypoint already defaults `.sql` files to
   `MARIADB_DATABASE`, but `USE` makes it unconditional.
3. **`initdb/02-seed.sql`**: byte-copy of `../od-api-linux/seed.sql` + the same
   `USE` line. Unchanged semantics: the seeded `apisubscription.EndPointUrl`
   (`http://localhost:8080/__test/sink`) still works because the events poller
   runs *inside* `od-api` — `localhost:8080` is its own Kestrel.
4. **`images-perms` one-shot**: codifies the lab's manual `chmod 777` of the
   share root (fresh named volumes are root-owned; first `put` fails
   `NT_STATUS_ACCESS_DENIED` without it).
5. **Health-gated ordering** (`service_healthy` / `service_completed_successfully`)
   replaces the lab's manual sequencing. No owned file needed a healthcheck or
   retry loop added to it.

## What a demo-exposed-ports variant would change

One line in `docker-compose.yml`, on `od-tls-proxy` only:

```yaml
    ports: ["8443:443"]
```

then `https://<host>:8443/...` (still `curl -k`: the cert is `tls internal`,
self-signed). Nothing else changes — and these MUST stay internal even in demo
mode: MariaDB 3306 (per vendor hardening: never past the LAN), `od-api:8080`
(plaintext ODFHIR credentials; `/__test/sink` has no auth by design), Samba 445.
A real demo also wants `tls internal` replaced with a proper trust chain (see
`../od-api-linux/tls/TLS.md` production-cutover notes) — that is a Caddyfile
*override* in this directory, never an edit to the owned file.

## Provenance / safety

- Branch `port/od-api-linux`, uncommitted; live lab containers on `fleet-r02`
  (`od-api`, `od-mariadb`, `od-samba`, `od-tls-proxy`, `od-mt` on `odnet`) are
  owned by other lanes — this stack never joins `odnet`, never sets
  `container_name:`, never publishes ports. Verified by `docker ps` before and
  after (see task report).
- Tested `2026-10-20` on `fleet-r02` as project `odstack`, torn down with
  `docker compose down -v` afterwards (proof in the task report).
- Timeboxed spike: if composition resists (e.g. entrypoint/Caddyfile behaviour
  drift), the fix belongs here as a copy/override, and gets recorded above.
