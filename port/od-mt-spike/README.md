# od-mt-spike — Kestrel middle-tier route (spike, stub processor)

Answers: can a Linux host accept the tier's wire input and drive it toward `DtoProcessor`?
Answer: **route yes, pipeline needs the business-layer port** (inventory below).

## Wire anatomy (from `OpenDentBusiness/WebServices/OpenDentalServerReal.cs` + ASMX shape)

- Route: `POST /ServiceMain.asmx` (alias is an IIS concept; Kestrel takes `MT_ALIAS`, default `""`).
- Body: SOAP 1.1 envelope, `ProcessRequest` in `http://www.open-dent.com/OpenDentalServer`,
  single `dtoString` param carrying the DTO XML with one level of XML escaping.
  `SOAPAction: http://www.open-dent.com/OpenDentalServer/ProcessRequest`.
- Success: HTTP 200 + `ProcessRequestResponse/ProcessRequestResult` (inner DTO escaping intact —
  the DTO layer's `XmlEscapeRecursion` is removed by the DTO layer, not the transport).
- Transport failure: SOAP Fault + HTTP 500. App failure: HTTP 200 carrying a serialized
  `DtoException` DTO (client rethrows). `GET ?wsdl` serves the transcribed contract.
- Deltas from vendor IIS setup (documented, not silent): 64 MB body cap (vendor ~1 GB for
  imaging); culture pinning and Windows Auth/client-cert mapping not implemented.

## The three shims (only System.Web coupling in the path)

`MT_CONFIG_ROOT` → `Server.MapPath(".")`; `MT_ALIAS` → `HostingEnvironment.ApplicationVirtualPath`
multi-DB routing; per-request `RemoteIpAddress` → `HttpContext.Current...UserHostAddress` (log only).

## Real-slice compile attempt (bounded, net8.0, 11 Remoting/*.cs + Newtonsoft + MySqlConnector)

**38 errors, three classes, no surprises:**
- 32× CS0246 missing in-tree namespaces (`CodeBase`, `DataConnectionBase`, `ODException`, …) —
  the cascade into the 1,959-file business library, which is the actual port.
- 2× CS0234 `System.Web` (`Hosting`, plus `HttpContext` in DtoProcessor) — the documented shims.
- 4× CS1069 `System.Drawing.Bitmap` → fixable with one `System.Drawing.Common` PackageReference.
- Notably absent: no WinForms/WPF/COM errors *in this folder* — the Remoting layer itself is
  portable modulo the above; `CodeBaseStandard`/`DataConnectionBaseStandard` (netstandard2.0,
  already in-tree) are the natural next slice to extend.

## `IDtoProcessor` seam + stub contract

`StubDtoProcessor` dispatches on DTO root name exactly like `DtoProcessor`'s `type ==` chain and
answers shape-correct payloads (`0`/`True`/`1`/table XML/echo); unknown roots get a serialized
`DtoException` DTO — precisely what a real client turns back into a thrown exception. Swap the
registration in `Program.cs` for a real adapter; no route changes needed.

## Run / verify (odnet-internal, disposable)

```bash
docker build -t od-mt-spike .
docker run -d --name od-mt --network odnet od-mt-spike
# SOAP round-trip (DtoGetString → echo):
curl -X POST http://od-mt:8080/ServiceMain.asmx -H "Content-Type: text/xml" \
  -H "SOAPAction: http://www.open-dent.com/OpenDentalServer/ProcessRequest" --data @req.xml
# req.xml: SOAP 1.1 envelope with ProcessRequest/dtoString (see ServiceMain.wsdl for shape)
```

Two fidelity fixes applied during verification: `ServiceMain.wsdl` is now copied to output
(`?wsdl` was 500), and all envelope builders use the `Utf8Writer` (responses declared utf-16).

## Standard-slice progress (2026-10-20, branch port/od-api-linux, uncommitted)

Both in-tree Standard slices now build clean with `dotnet build` (netstandard2.0):
- `CodeBaseStandard/xCodeBaseStandard.csproj` — 0 errors (was already clean; no changes needed).
- `DataConnectionBase/xDataConnectionBaseStandard.csproj` — 0 errors (was 16 unique errors).
  Three minimal, ifdef-gated diffs (Windows builds byte-identical in behavior, +19 lines total):
  1. `MySqlHelper.cs`: unused `using System.Web.UI.WebControls.WebParts;` wrapped in
     `#if !DOT_NET_STANDARD` (same using-gating precedent as `SOut.cs`).
  2. `xDataConnectionBaseStandard.csproj`: added `PackageReference MySqlConnector 2.4.0` —
     the same OSS version the Windows business layer already uses (`DataConnectionBase/packages.config`);
     the `MySql.Data` blob references do not provide the `MySqlConnector` namespace that
     `DataConnection.cs`/`MySqlHelper.cs` compile against. Not a new blob.
  3. `ConnectionStoreBase.cs`: the two `CDT.Class1.Decrypt` call sites
     (`InitConnectionStoreXml`, `InitOpenDentalWebConfigXml`) throw explicit
     `NotSupportedException` under `DOT_NET_STANDARD` with a comment recording that CDT.dll is a
     closed Windows-only blob with no Linux execution (callers must supply connection details
     out-of-band). `#else` branches keep the Windows code path exactly as before; no blob code
     copied, no blob constants embedded. (`GetWebAppPath()` already stubbed the same way.)

Remoting-slice verification (temp net8.0 project OUTSIDE the repo, same 11 `Remoting/*.cs` +
Newtonsoft.Json 13.0.3 + MySqlConnector 2.4.0 as the baseline above, plus ProjectReferences to the
two slices; temp dirs deleted after the run):
- Before: 38 error lines / 19 unique — 16× CS0246 (7 `using CodeBase`, 2 `using DataConnectionBase`,
  1 `ODException`, 5 `MiddleTierRole`, 1 `IOpenDentalServer`), 1× CS0234 (`System.Web.Hosting`),
  2× CS1069 (`System.Drawing.Bitmap`).
- After: 18 error lines / 9 unique — every slice-attributable diagnostic is gone
  (10 CodeBase/DataConnectionBase/ODException errors → 0). A probe file calling every
  CodeBase/DataConnectionBase member the Remoting folder touches (`ODInitialize`, `ODThread`,
  `ODException`, `ODBuild`, `MiscUtils`, `ODFileUtils`, `Logger`, `ODEvent`, `DataConnection`,
  `QueryMonitor`) builds with 0 errors against the slices from net8.0, so slice coverage is proven
  at member level, not just namespace level.

Residual inventory (what still blocks the Remoting folder):
- Reported by the compiler (9 unique): 1× CS0234 `System.Web.Hosting` (DtoProcessor:10, Kestrel
  shim — other lane), 5× CS0246 `MiddleTierRole` + 1× CS0246 `IOpenDentalServer` (both are tiny
  dependency-free business leaves: `OpenDentBusiness/Cache/EnumMiddleTierRole.cs`,
  `OpenDentBusiness/WebServices/IOpenDentalServer.cs`), 2× CS1069 `System.Drawing.Bitmap`
  (WebSerializer:775,959 — the deferred `System.Drawing.Common` package reference).
- NOT reported but still real (suppressed diagnostics — see note): `System.Web.HttpContext`
  (DtoProcessor:280, Kestrel shim — other lane) plus the business-layer cascade the port must absorb
  next: `Security`, `Userods`, `Userod`, `Db`, `Signalods`/`Signalod`, `PrefC`/`PrefName`, `Reports`,
  `Programs`, `Plugins`, `PIn`, and `CDT.Class1.Decrypt` in `DtoProcessor` (same stub treatment as
  `ConnectionStoreBase` when the business port reaches it).
- Diagnostic note for the next attempt: files containing certain constructs (observed in
  `Meth.cs`/`DtoProcessor.cs`) suppress expression-level unknown-name diagnostics compilation-wide
  while declaration/type-position diagnostics still report — so the 9 reported errors understate the
  cascade. Per-file compiles + the coverage probe above are the reliable inventory method until the
  business leaves land.

Precise next step: pull the two dependency-free leaves (`EnumMiddleTierRole.cs`,
`IOpenDentalServer.cs`) into the slice verification to isolate the residual to
Web-shims + Drawing + the business cascade — then start the business-layer port at `Db`/`Security`,
which `DtoProcessor.ProcessDto` cannot initialize without.
