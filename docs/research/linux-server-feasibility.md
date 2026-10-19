# Open Dental on Linux — Eliminating Windows Server: Research Findings

**Date:** 2026-10-19 (UTC)
**Repo:** https://github.com/MTG-Thomas/opendental · branch `research/linux-server-feasibility`
**Base commit at investigation:** `adedfb0` ("Prior research on middle-tier added")
**Scope:** curiosity-driven spike, timeboxed to a few hours. No production systems touched, synthetic data only.
**Reference installer present in workdir (not committed):** `TrialDownload-25-3-48.exe` (~143 MB, Open Dental 25.3 trial-era).
**Companion prior work in repo:** `MIDDLE_TIER_ARCHITECTURE_ASSESSMENT.md` (2026-04-04, branch `24_3` @ `d804c19`).

> Central question: *Can we eliminate Windows Server entirely, keeping Windows only for
> clinical workstations and proprietary integrations? If not, what is the smallest Windows
> footprint and why?*
>
> Short answer: **yes for database + files today (vendor-supported), no for background
> services without Windows.** The smallest viable architecture is a **hybrid**: Linux hosts
> MariaDB + OpenDentImages (Samba), while a minimal Windows host runs the background
> services (OpenDentalService, eConnector, HL7) and optionally the IIS middle tier.
> A full-Linux port is a multi-week/month port, not a rehost — blocked by closed-source
> committed binaries (`ODCrypt.dll`, `CDT.dll`), WinForms/WPF/COM entanglement in the shared
> business library, and the absence of `OpenDentalService`/`EConnector` source in this GPL snapshot.

---

## 1. Baseline

### 1.1 Version, lineage, license

| Item | Finding |
|---|---|
| Version in this snapshot | **24.3.63.0** — corroborated three ways: `db/minimum-data.sql` contains `('DataBaseVersion','24.3.63.0',...)`; committed `CDT.dll`/`ODCrypt.dll` report `Version=24.3.63.0` via Mono `monop`; git history is a chain of `version 24.3.x` commits on top of `d804c19 version 24.3.63.0`. |
| Upstream lineage | `OpenDental/opendental` branch `24_3` (v24.3.x) → fork `etnguyen03/opendental` (Feb 2025, "last GPL version distributed," goal: easier dev, Linux build, k8s, CI/CD, remove DB-integrity/security-by-obscurity + cloud services) → this fork `MTG-Thomas/opendental` plus local commits (NuGet upgrades, DLL cleanup, `db/` skeema + seed, `MIDDLE_TIER_ARCHITECTURE_ASSESSMENT.md`). |
| License | **GPLv2** (`LICENSE`, 1991 FSF text; README attributes © 2003–2024 Jordan S. Sparks, DMD + © 2025 Ethan Nguyen and contributors). BUT the tree mixes in non-GPL/closed bits — see §1.4 and §5.3. Do not assume bundled binaries are freely redistributable. |
| Trial installer (25.3.48, proprietary era) | `TrialDownload-25-3-48.exe` is a **PE32 i386 SFX RAR** (`Rar!` overlay at offset 389120, inner `Setup=.\Release\OpenDentalInstaller.exe`). String scan of the SFX lists a `Release/` payload of client installer + `CDT.dll, CodeBase.dll, DataConnectionBase.dll, MySqlConnector.dll, Newtonsoft.Json.dll, ODCrypt.dll, WebView2, Tao.*, VBbridges` plus `Release/Databases/{blank,canada}/...` **MyISAM** table files (`.frm/.MYD/.MYI`) and `Release/GrantTables`, `Release/sys`. No `OpenDentalService.exe` / `OpenDentalEConnector.exe` strings found in the stub — it is a client+DB bootstrap, not a services bundle. Not committed (143 MB untracked, left out of git deliberately). |

### 1.2 Server-side project inventory (what is actually in the solution)

`OpenDental.sln` contains **no** `OpenDentalService` or `OpenDentalEConnector` project. The only
`ServiceBase` subclasses in the tree are `OpenDentHL7.ServiceHL7` (+ legacy `EcwOld.cs` duplicate).
Service *management* exists, service *implementations* (for the two named services) do not.

| Project / area | Role | Framework | Hosting / notes |
|---|---|---|---|
| `OpenDentalServer` | Thin ASP.NET **ASMX** middle-tier host (`ServiceMain.asmx` + ~10-line `ServiceMain.asmx.cs` → `DtoProcessor.ProcessDto`) | net48, Web Application GUID | IIS-only as shipped; 3 `System.Web` call sites total (see §2) |
| `OpenDentBusiness` | Shared business logic + DTO processing (`Remoting/DtoProcessor`, `Data Interface/` S-classes, `Crud/`, `TableTypes/`, `Eclaims/`, `X12/`, `HL7/`, `Email/`, `Bridges/`, `WebServices/`) — **1,959 `Compile Include`, ~3,089-line csproj** | net48, `AllowUnsafeBlocks=true`, LangVersion 8.0 | Referenced by server AND thick client; carries client-only Windows refs (§2) |
| `CodeBase` (xCodeBase) | Shared utils (threading, files, `ServicesHelper` for Windows SCM) | net48 | `ServicesHelper` uses `ServiceController`, `Registry.LocalMachine`, `System.Management`, `installutil.exe` — Windows-only service install/start |
| `CodeBaseStandard` | netstandard2.0 slice of CodeBase utils (`DOT_NET_STANDARD` defines) | **netstandard2.0** (SDK-style) | Prior cross-platform scaffolding; only ~14 files linked |
| `DataConnectionBase` | DB abstraction (`DataConnection.cs`, `DbAdminMysql.cs`) + `CDT` ref | net48 | `MySqlConnector 2.4.0`; otherwise thin |
| `DataConnectionBaseStandard` | netstandard2.0 slice (links CodeBaseStandard) | **netstandard2.0** | Scaffolding only; still references `Required dlls\CDT.dll` + `.NET Standard\` shims |
| `OpenDentHL7` | **Windows Service** (`ServiceHL7 : ServiceBase`, `Program.cs` SCM lookup, `ProjectInstaller.cs`) + `FormDebug` | net48, `WinExe` | `System.ServiceProcess`, `System.Windows.Forms`, `EventLog.WriteEntry`, `Registry`, `Application.StartupPath/FreeDentalConfig.xml`; SFTP via `Tamir.SharpSSH`, `CDT.dll` |
| `ServiceManager` | WinForms tool to install/manage `OpenDentalService`, `OpenDentalEConnector`, `OpenDentHL7`, `OpenDentalReplicationService`, `MySQL`, `OpenDentalAPIService` | net48 | Expects `...\Open Dental\OpenDentalService\OpenDentalService.exe` etc. — manages exes **not present** in this source snapshot |
| `MobileWeb` | Legacy ASP.NET WebForms mobile UI | **net45**, Web Application GUID | IIS/WebForms; obsolete path, not a services replacement |
| `OpenDentalCloud` | Cloud storage backends (Dropbox, SFTP via Renci.SshNet) | net48 | Library, no hosting |
| `OpenDentalGraph` / `CentralManager` / `QueryExecutor` / `MySQLManager` | Dashboard lib / multi-office manager / query tool / MySQL admin | net48 (ODR 4.7.2) | Client-side tools, not server daemons |
| `OpenDental` (main) | Thick WinForms/WPF client **+ in-client background threads** (`FormOpenDentalThreads`: signal timer, `EServiceMonitorThread`, `ODServiceMonitorThread`, `ODServiceStarterThread` via `ServicesHelper.StartServices`) | net48 | Many eService duties in 24.3 run as client threads + `eservicesignal`/`signalod` rows, not as standalone daemons |
| `db/` | `skeema/` (408 table defs) + `minimum-data.sql` (2,422 lines seed) + `.skeema.SAMPLE` (`flavor=mariadb:10.5`) | — | Schema is the real cross-platform contract (§4) |
| `packaging/` | 2007-era Debian/Mono packaging (`opendental.sh` → `/usr/bin/cli /usr/lib/opendental/OpenDental.exe`, `debian/rules`, live-CD) | Mono 1.2.x era | Historical only; predates net48 by a decade |

**Missing vs. the task brief:** there is no `OpenDentalService`, `OpenDentalEConnector`,
`OpenDentalReplicationService`, `OpenDentalCustListener`, or `OpenDentalAPIService` *source*
in 24.3 GPL. References exist only as strings/config: `FormEServicesEConnector` checks
`ServicesHelper.GetServicesByExe("OpenDentalEConnector.exe")`; `FormServiceManage` special-cases
`OpenDentalEConnector.exe`/`OpenDentalService.exe`; `FormWebConfigSettings` edits
`OpenDentalServiceConfig.xml`/`OpenDentalWebConfig.xml`; per-DB multi-service folders are documented
in vendor manuals. **Conclusion: in this snapshot those services are proprietary add-ons
installed beside the client, not buildable from this source.** Dependency analysis therefore
recommends *against* starting a PoC from `OpenDentalService`/eConnector source — there is none —
and points at `OpenDentHL7` + `OpenDentalServer` + DB/files as the testable surface.

### 1.3 Target frameworks, deps, build

- **Everything ships net48** except `MobileWeb` (net45), `ODR` (net47.2), and the two
  `*Standard` netstandard2.0 slices. `OpenDentBusiness` pins LangVersion 8.0.
- Data layer is modern where it counts: **`MySqlConnector 2.4.0`** (cross-platform, also ships
  `Microsoft.Extensions.* 8.x`, `System.Text.Json 8.0.6` side-by-side with Framework 4.8).
- `OpenDentBusiness.csproj` reference highlights (full list in §A): `Bridges (x86)`, `CDT`,
  `Interop.Word`, `SigPlusNET`, `Sparks3D`, `RS232`, `VirtualWeb`, COM `ADODB`/`CDO`,
  plus Framework refs `System.Windows.Forms`, `PresentationCore/Framework`, `WindowsBase`,
  `System.Xaml`, `System.DirectoryServices`, `System.EnterpriseServices`, `System.Management`,
  `System.ServiceProcess`, `System.Web*`. Any one of the WPF/COM/ServiceProcess refs is enough
  to fail a naive `dotnet build` on Linux; the set together makes it a port, not a rehost.
- Build requires **Windows + MSBuild + NuGet restore** (`.github/workflows/dotnet-desktop.yml`:
  `windows-2022`, `setup-msbuild`, `nuget restore`, `MSBuild .\OpenDental.sln`). Verified locally:
  BuildTools MSBuild `17.14.60`, .NET SDK `8.0.426` present; `mono` absent on host (used via container).
- `db/README.md` pins **MariaDB, latest supported 10.5 at time of writing** (author wants newer);
  `.skeema.SAMPLE` uses `flavor=mariadb:10.5`. Vendor today tests 10.5.11 / 10.5.19 / **10.11.10**
  (installer default) and states 10.6+ needs OD ≥ 24.3 — this snapshot qualifies.

### 1.4 Committed binaries — classification

Method: PE COM-descriptor (`com_rva != 0` ⇒ managed) + machine field, over `Required dlls/`
(88 entries) and `RequiredServerDlls/` (batch files + `installutil.exe`). Framework targets
spot-checked via TFM strings. Full table in §A; summary:

- **~79 managed / AnyCPU-or-x86 .NET assemblies**, nearly all `machine=x86` headers (IL is
  portable; the x86 marker reflects the legacy build, and three refs are explicitly
  `processorArchitecture=x86`: `Bridges`, `Tao.*`, old `SigPlusNET` in CodeBase).
- **Native (no CLR header), all x86:** `EZT4Jpeg.dll`, `EZT4Pdf.dll`, `Eztwain4.dll` (TWAIN imaging),
  `MSVCR71.dll`, `SuniUSB.dll`, `VideoCamLib.dll`, `WebView2Loader.dll`, `freeglut.dll`,
  `vcruntime140.dll`, plus `Sparks3DXEngine.dll` (native; sibling `Sparks3D.dll` is managed).
  These are client imaging/3D/WebView2 concerns — none needed by a headless service, but they
  ship in the same folder and explain the historical "Enable 32-Bit Applications = True" guidance.
- **Notable managed-but-closed:** `CDT.dll` (24.3.63.0, net48, ADA procedure-code crypto/licensing;
  `VerifyCallingAssembly`, `GetADA*`, `GetADAcodes`), `ODCrypt.dll` (24.3.63.0, net48, Dotfuscator-obfuscated;
  used by auth, imaging, session tokens, eConnector stats). **Neither has source in this repo**
  (§B) yet both are referenced by `DataConnectionBase`/`OpenDentBusiness`/HL7. Redistributability unknown —
  treat as proprietary blobs (CDT ships with an End-User License rtf/txt in `OpenDental/Resources/`).
- `RequiredServerDlls/` contains **no service binaries** — only `Batch*.bat` + `installutil.exe`.
  The only committed EXE is `RdlDesigner.exe` (managed report designer; fails headless under Mono
  with missing `RdlEngine` unless co-located — expected, §4.4).
- Cross-version note: csproj asks `ODCrypt 18.2.7.0`, binary reports `24.3.63.0` — version drift
  between project metadata and the bundled blob; another reason to treat blobs as baselines, not APIs.

---

## 2. Server architecture map — what actually requires Windows

| Component | Can it run on Linux? | Evidence / constraints |
|---|---|---|
| **MariaDB/MySQL** | **Yes — natively. This is the vendor-supported Linux role.** | Vendor `linuxinstall.html`: Linux server hosts MariaDB + image files; support will not help install it (IT-owned). Tested: 10.5.11/10.5.19/10.11.10; 10.6+ needs OD ≥ 24.3. Required `server.cnf`: `default-storage-engine=MyISAM`, `max_allowed_packet=40M`, `max_connections=3000`, `port=3306`, `sql_mode=''`, `explicit_defaults_for_timestamp=1`; on Linux add `lower_case_table_names=1` (case-sensitivity bug class: `ehrTrigger` vs `ehrtrigger`). All 408 `db/skeema/*.sql` tables are `ENGINE=MyISAM DEFAULT CHARSET=utf8 COLLATE=utf8_general_ci`. PoC §4.2 confirms MariaDB 10.11 runs them unmodified. |
| **OpenDentImages (A-to-Z folder)** | **Yes via Samba/NFS.** | Vendor: "just like any other fileshare… all users need full permissions (read, write, delete)"; Webmin+Samba+`chmod -R 777` recipe. Semantics needed: create/read/write/delete, stable share path in `Data Paths`, case-insensitive-friendly naming (commit `a634d17` lowercases extension checks; app otherwise assumes Windows case-insensitivity — PoC §4.3 demos the `Abc` vs `abc` trap). `ImageStore.GetPatientFolder` also supports In-Database and Cloud AtoZ, which sidestep share semantics at cost/ops trade-offs. |
| **OpenDentalServer (middle tier)** | **Rehostable; Linux port is a small-host + big-library problem.** | Hosting glue is 3 `System.Web` call sites: `ServiceMain.asmx.cs` (`WebService` base + `Server.MapPath(".")`), `DtoProcessor.cs:64` (`HostingEnvironment.ApplicationVirtualPath`), `:280` (`HttpContext.Current.Request.UserHostAddress`, non-critical log path). `IOpenDentalServer` + `OpenDentalServerMockIIS` already prove `DtoProcessor.ProcessDto(dtoString, mapPath)` works without IIS. The blocker is not ASMX — it is `OpenDentBusiness.dll`'s client-only refs (WinForms/WPF/COM/x86/native) that must resolve at load time, plus `System.Drawing` in `WebSerializer.cs` (GDI+). Prior assessment (`MIDDLE_TIER…§6`) grades Windows-container/IIS-free-Windows as low/medium effort, Linux as a **port** (weeks–months). |
| **OpenDentalService** | **Windows-only; source absent in 24.3 GPL.** | Vendor `opendentalservice.html`: runs aging, email fetch, clearinghouse import, recurring/repeating/payplan charges, CareCredit, Podium invites, portal/WebSched alert creation. Installed from `…\Open Dental\OpenDentalService\OpenDentalService.exe` + `OpenDentalServiceConfig.xml` (ServiceManager). In 24.3 source the equivalent duties are split between client threads (`FormOpenDentalThreads`) and DB signals — there is no portable daemon to lift. |
| **eConnector** | **Windows-only; explicitly "not compatible with Linux or Mac."** | Vendor `econnector.html`: required for *all* eServices; bridges office ↔ OD HQ secure servers; local Listener + firewall rule `OpenDental EServicesListener`; heartbeat/error rows in `eservicesignal`. Needs always-on Windows + Update-Server role. Depends on proprietary HQ endpoints — out of scope for a Linux PoC by design (no production eServices contact). |
| **API services** (`OpenDentalAPIService`, `ODApi.dll`) | **Windows-only as shipped.** | ServiceManager lists `OpenDentalAPIService` for third-party vendors; `ODApi.dll` is a committed managed blob (no source project). No Linux host documented. |
| **HL7 (`OpenDentHL7`)** | **Windows-only as shipped; closest to portable in shape, not in deps.** | `ServiceBase` + `EventLog` + `Registry` + `WinForms` + `FreeDentalConfig.xml` + `Tamir.SharpSSH` + `CDT.dll`. Transports (file drop, MLLP TCP, SFTP sider) are inherently portable concepts, but the hosting (SCM, Event Log, machine-name-bound HL7Def/eCW checks) and `CDT`/`ODCrypt` deps are not. Only `ServiceBase` subclass in the tree — i.e., the *only* real Windows daemon in source. |
| **Replication / CustListener** | **Windows-only / legacy.** | Names appear only in ServiceManager + docs (`OpenDentalReplicationService`, `OpenDentalCustListener` = pre-eConnector listener). No source. Multi-DB hosting = one folder copy per DB + matching versions. |
| **Email / X12 / Eclaims / bridges** | **Mixed; logic portable, edges Windows-bound.** | Managed C# with `MailKit`/`MimeKit`/`OpenPop` (portable) but bordered by COM (Word merge via `Interop.Word`), NDde/VBbridges, RS232/signature-pad SDKs, QuickBooks `Interop.QBFC10`, TWAIN native DLLs. Server-side paths (fetch mail, import reports, run charges) avoid most of this — but the assembly refs travel with them. |

**Smallest independently runnable pieces:** (1) MariaDB + files — already independent;
(2) `DtoProcessor.ProcessDto` behind any HTTP host — independent once `System.Web` shims + Windows
refs are resolved; (3) HL7 file/TCP loop — independent in theory, Windows-bound in practice.
Everything else (Service, eConnector, API) is a Windows host + proprietary HQ relationship.

**Architectural vs. historical:** IIS/ASMX, SCM/EventLog/Registry hosting, `Server.MapPath`,
and the x86 app-pool flag are *packaging choices*. MyISAM-everywhere, `sql_mode=''`, and the
DTO-over-SOAP wire shape are *architectural* (client↔server version coupling lives in
`DtoProcessor` version checks + `ConvertDatabases*` migrations). The WinForms-in-`OpenDentBusiness`
split is historical layering debt, but `ODCrypt`/`CDT` closed-source is a hard architectural fact
for any port.

---

## 3. Linux runtime options — evaluation

| Approach | Verdict | Complexity / reliability / licensing / maintenance |
|---|---|---|
| **1. Native modern .NET (port `OpenDentBusiness` et al. to net8+)** | **Feasible but large.** Preferred *if* a port is funded. | Must untangle WinForms/WPF/COM/ServiceProcess/DirectoryServices refs, replace `System.Drawing` (WebSerializer + imaging) with SkiaSharp/ImageSharp, conditionalize or extract client-only code, and — critically — replace or relicense **`ODCrypt` + `CDT`** (no source, obfuscated, license-encumbered). `MySqlConnector`/`MailKit`/modern `Microsoft.Extensions.*` already cross-platform. Reliability high once done; maintenance = tracking upstream proprietary releases without upstream cooperation (post-GPL drift). Weeks–months, not days. |
| **2. Mono (run Framework assemblies on Linux)** | **Loads metadata, fails executing core blobs. Not viable for services.** | PoC §4.4: Mono 6.12 `monop` reads `CDT`/`ODCrypt` fine, but **every executed `ODCrypt` method throws `InvalidProgramException: Invalid IL … pop`** (Dotfuscator IL that Framework JIT tolerates, Mono rejects). `RdlDesigner.exe` fails on missing co-located deps. Even if app code ran, `ServiceBase`/`EventLog`/`Registry`/`WinForms` need `mono-service`/X11 shims. Licensing unchanged (blobs still required). Fragile + obsolete runtime — do not build on it. |
| **3. Wine / Wine-Mono (unmodified Windows binaries)** | **Untested here; plausible for client-era binaries, wrong tool for servers.** | Would run `OpenDentalService.exe`/HL7 exes only with a Windows-like prefix, X/Display or headless hacks, and persistent Wine prefix ops burden. No evidence in-repo; vendor eConnector explicitly excludes Linux/Mac (suggesting HQ/version checks may also resist). Security/updates story poor for a server. Documented as *next step*, not recommended path. |
| **4. Hybrid (Linux DB + files, minimal Windows integration host)** | **Recommended. Smallest viable architecture today.** | Linux: MariaDB 10.11 + Samba share (both vendor-blessed). Windows (one VM/host, can be small): `OpenDentalService`, `eConnector`, `OpenDentHL7`, optionally IIS middle tier for remote offices. Keeps proprietary/HQ/licensing surface on Windows where the vendor supports it; Linux does what it is good at. Maintenance ≈ current Windows ops minus DB/file-server Windows licensing, plus Samba/MariaDB Linux ops (IT-owned, unsupported by OD). |

Prior in-repo assessment agrees (Windows container → IIS-free Windows → Linux port, in that order).
This spike adds the missing binary-compatibility data point (Mono fails on `ODCrypt`) and the
missing source-availability data point (Service/eConnector not in GPL tree), both of which push
the Linux-port option further out and the hybrid option forward.

---

## 4. Proof of concept (bounded, synthetic data only)

All commands below were run from the repo root on Windows with Docker Desktop (Linux engine).
No production endpoints contacted; disposable `opendental` database; PoC rows tagged `poc-linux`.

### 4.1 Environment

- Host: Windows 11, Docker Desktop 4.95 (Engine 29.8.2, linux/amd64), .NET SDK 8.0.426, BuildTools MSBuild 17.14.60.
- Linux images pulled: `mariadb:10.11`, `mcr.microsoft.com/dotnet/sdk:8.0` + `runtime:8.0`, `mono:6.12` (`mono:latest`), `ubuntu:22.04`.

### 4.2 MariaDB runs natively — schema applies unmodified ✅

```powershell
docker run --name od-mariadb -e MARIADB_ROOT_PASSWORD=root -e MARIADB_DATABASE=opendental -p 3307:3306 -d mariadb:10.11
docker exec od-mariadb mariadb -uroot -proot -e "SELECT VERSION();"
# → 10.11.19-MariaDB-ubu2204
cat db/skeema/preference.sql db/skeema/signalod.sql db/skeema/eservicesignal.sql db/skeema/alertitem.sql `
  | docker exec -i od-mariadb mariadb -uroot -proot opendental
docker exec od-mariadb mariadb -uroot -proot -e "USE opendental; SHOW TABLES; SELECT TABLE_NAME,ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='opendental';"
# → alertitem/eservicesignal/preference/signalod, all MyISAM
```

Observed deltas vs. vendor `server.cnf` (stock `mariadb:10.11` image): `default_storage_engine=InnoDB`
(want MyISAM), `max_allowed_packet=16M` (want 40M; `SET GLOBAL` did not persist to 40M in-session —
set via config file + restart for production), `max_connections=151` (want 3000; `SET GLOBAL` works),
`sql_mode=STRICT_TRANS_TABLES,...` (want `''`), `lower_case_table_names=0` (want `1` on Linux).
None blocked the PoC; all must be set for a real deployment. Full 408-table `skeema` apply + `minimum-data.sql`
seed left as a documented next step (subset suffices to prove the contract; full seed contains demo practice data).

### 4.3 File-share semantics — Linux is case-sensitive, Windows is not ⚠️

```powershell
docker run --rm ubuntu:22.04 bash -c 'mkdir -p /tmp/odimages/Abc && touch "/tmp/odimages/Abc/XRAY_001.png" && ls /tmp/odimages/Abc/ && ls /tmp/odimages/abc/'
# → XRAY_001.png ; ls: cannot access /tmp/odimages/abc/: No such file or directory
```

Read/write/delete all work; **case sensitivity does not match Windows**. Mitigations already in-repo:
`lower_case_table_names=1` (DB) and `ToLower()` filename checks (`a634d17`); operationally, enforce a
naming convention + Samba `case sensitive = no` / `preserve case` review before migrating an existing
`OpenDentImages` tree. Samba itself was not stood up in this spike (standard recipe in vendor docs);
a `dperson/samba`-based share + Windows-client mount check is the natural 30-minute follow-up.

### 4.4 Genuine OD binaries on Linux via Mono — metadata loads, execution fails ❌ (valuable negative result)

```powershell
docker network create odnet; docker network connect odnet od-mariadb
docker run --rm -v "C:/path/to/opendental/Required dlls:/oddll:ro" mono:latest sh -c 'monop -r:/oddll/ODCrypt.dll 2>&1 | head; monop -r:/oddll/CDT.dll CDT.Class1 2>&1 | head -n 8'
# → ODCrypt 24.3.63.0 (Arrays, CryptUtil, Encryption, KeccakDigest, MD5, Sha3); CDT.Class1 (Encrypt/TryEncrypt/VerifyCallingAssembly/GetADA*…)
docker run --rm -v "C:/.../Required dlls:/oddll:ro" -v "C:/.../docs/research/poc/odcrypt-mono:/work" mono:latest sh -c 'cd /work && mono-csc crypttest2.cs -r:/oddll/ODCrypt.dll -out:crypttest2.exe && cp /oddll/ODCrypt.dll ./ && mono crypttest2.exe'
# → runtime=4.0.30319.42000 os=Unix 6.18.54.1
# → System.InvalidProgramException: Invalid IL code in ODCrypt.Encryption:StringToBytes … IL_00a5: pop
# Same for GenerateKey/EncryptString. RdlDesigner.exe → TypeLoadException (needs co-located RdlEngine).
```

(Note: Docker bind-mounts require the `C:/...` absolute form on this host; `$(pwd)`-relative mounts
with spaces in `Required dlls` silently fail — recorded here to save the next person 20 minutes.)

**Interpretation:** committed managed binaries are *Mono-readable* but `ODCrypt.dll` (Dotfuscator-obfuscated,
no source) is *Mono-unexecutable*. Since auth/imaging/session code calls `ODCrypt` on nearly every path,
**Mono is disqualified as a service runtime**, not merely inconvenient.

### 4.5 Minimal Linux service analogue — meaningful OD-schema work succeeds ✅

Because Service/eConnector source is absent and `ODCrypt` blocks Mono, the bounded PoC builds the
smallest honest thing: a **.NET 8 Linux worker using the same `MySqlConnector 2.4.0`** that performs
the same *kinds* of writes a background service performs (heartbeat + signal broadcast + pref touch)
and reads them back. Sources: `docs/research/poc/od-linux-heartbeat/` (`Program.cs`, `.csproj`, `Dockerfile`).

```powershell
docker build -t od-linux-heartbeat ./docs/research/poc/od-linux-heartbeat
docker run --rm --network odnet -e OD_DB_HOST=od-mariadb -e OD_DB_USER=root -e OD_DB_PASS=root od-linux-heartbeat
# connecting to od-mariadb:3306/opendental as root (MySqlConnector 2.0.0.0 [assembly version of pkg 2.4.0], runtime 8.0.32, OS Debian GNU/Linux 12 (bookworm))
# server version: 10.11.19-MariaDB-ubu2204
# heartbeat rows: 1
# signal rows: 1
# pref rows: 1
# readback: heartbeat=1 signal=1 pref=1
# POC_SUCCESS: genuine OD-schema writes + readback on Linux
```

Writes: `eservicesignal` heartbeat (`Tag='poc-linux'`), `signalod` invalidation (`MsgValue='poc'`,
mirroring `Signalods.SetInvalid`), synthetic `preference` marker (`LinuxPoCLastRun` — deliberately
*not* a real pref name, so no schema/behavior coupling). Readback asserts all three — a process
that merely starts would not satisfy this. This proves the **data plane is fully Linux-capable**;
what remains Windows-bound is the *service hosting + proprietary logic*, not MariaDB access.

### 4.6 What was deliberately not attempted

- Full 408-table + seed import (mechanical; `skeema` + `minimum-data.sql` documented, subset proves engine/charset).
- Samba share + Windows-client mount round-trip (standard; needs a Windows client or `smbclient` check).
- Wine execution of `OpenDentalService.exe`/HL7 (binaries not in this source snapshot; needs proprietary install + prefix ops).
- Rehosting `DtoProcessor` on Kestrel/`HttpListener` (design is 3 call sites; implementation + SOAP-envelope compat test is a follow-up work item, §6).
- Any contact with eServices HQ, real patient data, or trial-DB upgrades.

### 4.7 Reproducibility

```powershell
# DB (disposable; published port 3307 to avoid clashing with a local MySQL on 3306)
docker run --name od-mariadb -e MARIADB_ROOT_PASSWORD=root -e MARIADB_DATABASE=opendental -p 3307:3306 -d mariadb:10.11
# subset schema (full: loop db/skeema/*.sql; then db/minimum-data.sql)
# heartbeat worker
docker network create odnet; docker network connect odnet od-mariadb
docker build -t od-linux-heartbeat ./docs/research/poc/od-linux-heartbeat
docker run --rm --network odnet -e OD_DB_HOST=od-mariadb -e OD_DB_USER=root -e OD_DB_PASS=root od-linux-heartbeat
# Mono negative control
docker run --rm -v "C:/abs/path/Required dlls:/oddll:ro" -v "C:/abs/path/docs/research/poc/odcrypt-mono:/work" mono:latest sh -c 'cd /work && mono-csc crypttest2.cs -r:/oddll/ODCrypt.dll -out:crypttest2.exe && cp /oddll/ODCrypt.dll ./ && mono crypttest2.exe'
# cleanup
docker rm -f od-mariadb; docker network rm odnet; docker rmi od-linux-heartbeat
```

---

## 5. Compatibility and maintenance

### 5.1 Could a 24.3 GPL backend serve current (25.3, proprietary) clients? Assume no.

- The wire contract is **version-coupled**: `DtoProcessor` validates client/server versions and the DB
  migrates through `ConvertDatabases*` steps; `ServiceManager` refuses mismatched service versions
  ("will not start if the version is not exactly the same"). No cross-version matrix is published.
- Schema drift across ~2 major versions (24.3 → 25.3, incl. trial-DB improvements) is expected;
  silently upgrading schemas/deps was out of scope and not attempted.
- Proprietary clients may also enforce licensing/registration/HQ checks absent from GPL 24.3
  (this fork's stated goal is to *remove* such checks — further reason to keep version families matched).

### 5.2 Schema and migration risks

- Engine/charset contract is stable (`MyISAM`/`utf8` everywhere in `skeema`) — good for Linux.
- Risk areas: `lower_case_table_names`, `sql_mode=''`, `max_allowed_packet`, MyISAM-vs-InnoDB default;
  `ConvertDatabases*` one-way migrations; CDT-gated procedure-code content (copyrighted ADA codes
  deliberately excluded from seed — obtain/import separately, mind the EULA).
- The trial installer's `Databases/{blank,canada}` MyISAM file-copies show the vendor still ships
  file-level DB images — consistent with MyISAM but operationally fragile (copy-while-stopped only).

### 5.3 Licensing

- Code: GPLv2. Committed blobs: `CDT.dll` (ADA-copyrighted codes + EULA in `OpenDental/Resources/`),
  `ODCrypt.dll` (closed, obfuscated), ~20 third-party managed libs + ~10 native DLLs of mixed
  provenance (`Tamir.SharpSSH`, `NDde`, `SigPlusNET`, TWAIN, DirectX/SharpDX, WebView2 loader…).
  **Do not redistribute the tree as "open source" without a blob-by-blob audit.**
- Post-24.3 Open Dental is proprietary with source only under NDA for qualifying customers
  (vendor `sourcecode.html`); eServices/HQ are proprietary controls — no bypass attempted or advised.

### 5.4 Prior art — what already exists, what does not

| Source | Reusable? |
|---|---|
| `packaging/` + `BUILDING.Linux` (2007, Mono 1.2.5, `LINUX`/`MONO`/`DISABLE_*` flags) | Historical proof of intent; not buildable against net48. Do not revive directly. |
| `CodeBaseStandard` + `DataConnectionBaseStandard` (netstandard2.0, `DOT_NET_STANDARD` ifdefs) + `Required dlls/.NET Standard/` shims | Real scaffolding, but covers only utils + DB base; `OpenDentBusiness` itself was never sliced. Starting point for a port, not a shortcut. |
| `etnguyen03/opendental` goals (Linux build, k8s, CI/CD) | Aspirational at snapshot time — no k8s manifests found in history. This spike is the first executed Linux evidence in the fork lineage. |
| `freemed/opendental` mirror, `etnguyen03/opendental-patientportal` (Django, archived) | Context only. |
| Vendor docs (`linuxinstall`, `mariadb`, `opendentalservice`, `econnector`, `servicemanager`, `cloudhostedserver`, `apilocal`) | Authoritative for the hybrid boundary: **Linux = DB + files; Windows = services; Middle Tier = self-hosted Windows option for cloud.** No official Docker/K8s/Wine path. |
| `MIDDLE_TIER_ARCHITECTURE_ASSESSMENT.md` | Stands, with two amendments from this spike: (a) Mono empirically fails on `ODCrypt`; (b) Service/eConnector source absence makes "port the service" a non-starter from GPL. |

---

## 6. Recommended path, blockers, maintenance

### Smallest viable Windows-free-*server* architecture (recommended)

```
Linux (supported today)                 Windows (minimal, supported by vendor)
────────────────────────                ─────────────────────────────────────
MariaDB 10.11 (MyISAM, §4.2 config)     OpenDentalService (aging, mail, charges, alerts)
Samba share → OpenDentImages            eConnector (eServices/HQ bridge, Listener)
(optional) backups, monitoring          OpenDentHL7 (if HL7 feeds exist)
                                        IIS middle tier (only if remote/cloud clients need it)
LAN clients → MariaDB directly (classic) or via middle tier
```

Why this shape: it keeps every proprietary, HQ-coupled, license-checked, hardware-adjacent
workload where the vendor supports it, while moving the two heaviest undifferentiated pieces
(DB + files) to Linux. It requires **zero code changes** and matches vendor docs exactly.

### If the Windows footprint must shrink further (ordered, each a project)

1. **Windows-containerize the middle tier** (vendor-shape preserving; `mcr.microsoft.com/dotnet/framework/aspnet:4.8`),
   then **IIS-free Windows host** (`HttpListener`/Kestrel → `DtoProcessor.ProcessDto`, replicating the
   SOAP envelope or versioning the client). Kills IIS, not Windows.
2. **Extract a portable service core**: new net8 worker for the *schedulable, HQ-independent* duties
   (aging, alerts, recurring charges) against the OD schema, leaving eConnector/HQ flows on Windows.
   Requires replacing `ODCrypt` usage on those paths (e.g., platform crypto) and strict version-gating.
3. **Full Linux port** (last): slice `OpenDentBusiness` (client-only extraction), replace
   `System.Drawing`/COM/WPF, reimplement or license-replace `ODCrypt`/`CDT`. Multi-week/month effort
   with ongoing rebase cost against a proprietary upstream.

### Remaining blockers (specific, not speculative)

1. `ODCrypt.dll` — no source, obfuscated, Mono-unexecutable (`InvalidProgramException`), called from auth/imaging/session paths.
2. `CDT.dll` — no source, `VerifyCallingAssembly`, ADA-licensed content.
3. No `OpenDentalService`/`EConnector` source in GPL 24.3 — nothing portable to lift; eConnector vendor-declared Linux-incompatible + HQ-coupled.
4. `OpenDentBusiness` WinForms/WPF/COM/ServiceProcess references + `System.Drawing` (GDI+) + x86/native imaging DLLs.
5. `System.Web` 3-call-site middle-tier coupling (small, but SOAP-envelope compat must be proven against real clients).
6. Case-sensitivity + MyISAM/`sql_mode` operational config on Linux (solvable, must be done deliberately).

### Maintenance cost sketch

- **Hybrid:** ~current Windows-service ops + Linux MariaDB/Samba ops (IT-owned, vendor-unsupported). No rebase burden.
- **Port:** permanent fork maintenance — rebase/proprietary-drift review each OD release, blob re-validation
  (`CDT`/`ODCrypt` versions already drift from csproj metadata), plus imaging/HL7 edge upkeep. Only justified
  with a standing owner and a licensing resolution for the blobs.

---

## Appendix A — Binary inventory (committed, `Required dlls/`)

Method: PE DataDirectory COM-descriptor presence (managed vs native) + `IMAGE_FILE_MACHINE`.
Spot TFM via embedded `\.NETFramework,Version=` strings; versions via `monop` where noted.
`RequiredServerDlls/` = batch files + `installutil.exe` only (no service binaries).

| Binary | Class | Notes |
|---|---|---|
| `CDT.dll` (24.3.63.0) | Managed, x86, **net48**, closed, ADA-licensed | `CDT.Class1`: Encrypt/TryEncrypt/VerifyCallingAssembly/GetADA*/GetADAcodes; EULA in `OpenDental/Resources/CDT Content End User License.*` |
| `ODCrypt.dll` (24.3.63.0) | Managed, x86, **net48**, Dotfuscator-obfuscated, closed | `Encryption/Arrays/MD5/Sha3/KeccakDigest/CryptUtil`; **all tested methods throw `InvalidProgramException` on Mono 6.12** |
| `Bridges.dll` | Managed, **x86-only ref** | Likely source of 32-bit app-pool requirement |
| `Sparks3D.dll` / `Sparks3DXEngine.dll` (+`readme.txt`) | Managed / **native x86** pair | 3D charting; client-only |
| `EZT4Jpeg.dll`, `EZT4Pdf.dll`, `Eztwain4.dll`, `SuniUSB.dll`, `VideoCamLib.dll` | Native x86 | TWAIN/Imaging/hardware; client-only |
| `MSVCR71.dll`, `vcruntime140.dll`, `freeglut.dll`, `WebView2Loader.dll` | Native x86 | CRT/GL/WebView2 boot shims |
| `Interop.*` (Word, Acrobat, AcroPDF, WMP, QBFC10, VBIDE, AFORMAUT, CDR…) + `AxInterop.*` + `stdole.dll` | Managed interop | Office/QuickBooks/media bridges; Windows/COM-bound |
| `NDde.dll`, `VBbridges.dll`, `RS232.dll`, `SigPlusNET.dll`, `Tao.*`, `SharpDX.*`, `DirectX.*`, `MediaFoundation.*` | Managed wrappers over native/hardware | Signature pads, 3D, capture; client-only |
| `RdlDesigner.exe` (+`RdlEngine.dll`, `RdlViewer.dll`) | Managed | Report designer; needs co-located engine (Mono headless `TypeLoadException` when isolated) |
| `MySqlConnector` (pkg 2.4.0, asm 2.0.0.0), `Newtonsoft.Json` 13.0.3, `MailKit`/`MimeKit`, `OpenPop` 2.0.6.2, `Microsoft.Graph`, `AWSSDK`, `Google.Apis`, `PdfSharp`/`MigraDoc`, `Ionic.Zip`, `Renci.SshNet`, `Tamir.SharpSSH`, `log4net`, `System.*` 8.x shims | Managed, redistributable via NuGet | The portable core — same major versions run on Linux today (PoC uses MySqlConnector 2.4.0 on net8) |
| `PasswordVaultWrapper.dll`, `ODR.dll`, `ODApi.dll`, `Health.Direct.*`, `DpsPos.dll`, `IppDotNetSdk*`, `Avalara.*`, `Azure.*`, `ImagingDeviceManager.dll`, `xImageDeviceManager.dll`, `NHunspell.dll` | Managed | Payments, Direct messaging, vault, imaging; mixed platform needs |
| `installutil.exe` (+`RequiredServerDlls/Batch*.bat`) | Managed tool + scripts | SCM install helper, not a service |

`TrialDownload-25-3-48.exe`: intentionally **not** inventoried as a baseline binary (proprietary-era SFX stub, §1.1) and **not** committed.

## Appendix B — Where the closed blobs bite

- `DataConnectionBase.csproj → CDT.dll`; `OpenDentBusiness.csproj → CDT.dll + ODCrypt.dll`; also referenced by `CentralManager`, `DatabaseIntegrityCheck`, `ODR`, `OpenDental`, `OpenDentHL7`, `ServiceManager`, `UnitTests`.
- `ODCrypt.*` call sites include `Authentication.cs` (MD5/Sha3/salt/constant-compare), `SessionTokens`, `UserWebs`, `ImageStore`, ortho/payplan/procedure notes, `EConnectorStatistics`.
- `CDT.*` call sites include procedure codes, claims, payplans/paysplits, `PrefC`, registration keys, clearinghouse bridges, `DataAction`/`MiscData`, EService metrics.
- Net effect: **no meaningful service path avoids at least one closed blob.**

## Appendix C — Files added by this spike

- `docs/research/linux-server-feasibility.md` (this file)
- `docs/research/poc/od-linux-heartbeat/{Program.cs,od-linux-heartbeat.csproj,Dockerfile}` — .NET 8 Linux worker (PoC §4.5)
- `docs/research/poc/odcrypt-mono/{crypttest.cs,crypttest2.cs}` — Mono negative-control sources (PoC §4.4; built artifacts and copied DLLs excluded/removed)
- `docs/research/poc/README.md` — run/cleanup commands

## Appendix D — Key references

- In-repo: `MIDDLE_TIER_ARCHITECTURE_ASSESSMENT.md`, `db/README.md`, `db/skeema/.skeema.SAMPLE`, `db/minimum-data.sql`, `packaging/README`, `OpenDentalServer/ServiceMain.asmx.cs`, `OpenDentBusiness/Remoting/DtoProcessor.cs`, `OpenDentBusiness/WebServices/IOpenDentalServer.cs`, `OpenDentBusiness/WebServices/OpenDentalServerMockIIS.cs`, `OpenDentHL7/{ServiceHL7.cs,Program.cs}`, `ServiceManager/FormServiceManage.cs`, `CodeBase/Utilities/ServicesHelper.cs`, `CodeBaseStandard/`, `DataConnectionBase/xDataConnectionBaseStandard.csproj`.
- Vendor (fetched 2026-10-19): `linuxinstall.html`, `mariadb.html`, `opendentalservice.html`, `econnector.html`, `servicemanager.html`, `sourcecode.html`, `cloudhostedserver.html`, `clinuxmac.html`.
- History: `git log --oneline` (base `adedfb0`), `BUILDING.Linux @ cbfb4d0` (2007), `etnguyen03/opendental` (Feb 2025 fork statement).
