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
