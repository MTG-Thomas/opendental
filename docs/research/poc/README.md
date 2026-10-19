# Linux PoC — runbook (synthetic data only)

## 1. MariaDB on Linux (disposable)

```powershell
docker run --name od-mariadb -e MARIADB_ROOT_PASSWORD=root -e MARIADB_DATABASE=opendental -p 3307:3306 -d mariadb:10.11
docker exec od-mariadb mariadb -uroot -proot -e "SELECT VERSION();"
# subset schema (full: loop db/skeema/*.sql, then db/minimum-data.sql for seed)
cat db/skeema/preference.sql db/skeema/signalod.sql db/skeema/eservicesignal.sql db/skeema/alertitem.sql `
  | docker exec -i od-mariadb mariadb -uroot -proot opendental
```

Production `server.cnf` deltas (see parent doc §4.2): `default-storage-engine=MyISAM`,
`max_allowed_packet=40M`, `max_connections=3000`, `sql_mode=''`,
`explicit_defaults_for_timestamp=1`, plus `lower_case_table_names=1` on Linux.

## 2. .NET 8 Linux heartbeat worker (meaningful OD-schema writes + readback)

```powershell
docker network create odnet; docker network connect odnet od-mariadb
docker build -t od-linux-heartbeat ./docs/research/poc/od-linux-heartbeat
docker run --rm --network odnet -e OD_DB_HOST=od-mariadb -e OD_DB_USER=root -e OD_DB_PASS=root od-linux-heartbeat
# expect: heartbeat/signal/pref rows: 1 + POC_SUCCESS
```

## 3. Mono negative control (genuine OD binary, expected failure)

Needs absolute `C:/...` bind paths on this host (relative `$(pwd)` mounts with spaces fail).

```powershell
docker run --rm -v "C:/abs/path/Required dlls:/oddll:ro" -v "C:/abs/path/docs/research/poc/odcrypt-mono:/work" mono:latest sh -c 'cd /work && mono-csc crypttest2.cs -r:/oddll/ODCrypt.dll -out:crypttest2.exe && cp /oddll/ODCrypt.dll ./ && mono crypttest2.exe'
# expect: System.InvalidProgramException: Invalid IL code in ODCrypt.Encryption:...
```

## 4. Cleanup

```powershell
docker rm -f od-mariadb; docker network rm odnet; docker rmi od-linux-heartbeat
git status --short  # TrialDownload-*.exe must remain untracked; poc build outputs must not be committed
```
