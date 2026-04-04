# Open Dental Middle Tier Architecture Assessment

**Date:** 2026-04-04
**Repository analyzed:** OpenDental/opendental, branch `24_3` (v24.3.x)
**Commit:** `d804c19546233593d8a66af0591ed118a9e2c794`

---

## 1. Executive Summary

**IIS is not a hard architectural dependency. It is the currently supported packaging model.**

The Open Dental Middle Tier's actual IIS/ASMX coupling is a single ~10-line web service class (`ServiceMain.asmx.cs`) that delegates everything to `DtoProcessor.ProcessDto()` in the shared `OpenDentBusiness.dll` library. The project already has an `IOpenDentalServer` interface and a `OpenDentalServerMockIIS` class that proves the core DTO processing pipeline works completely outside of IIS. The entire Middle Tier transport layer could be replaced with a different HTTP host (Kestrel, HttpListener, etc.) with modest effort.

However, **Windows is a soft dependency** for the Middle Tier due to `OpenDentBusiness.dll` carrying references to Windows-only assemblies (System.Windows.Forms, PresentationFramework, System.DirectoryServices, COM interop DLLs like Interop.Word, signature pad hardware SDKs). Most of these are client-side concerns that bleed into the shared library because `OpenDentBusiness.dll` serves both the thick Windows client and the Middle Tier server. A Linux port would require either refactoring `OpenDentBusiness.dll` to separate client-only dependencies, or providing stubs/shims for unused Windows APIs.

**The most realistic near-term path is: Windows containerization (Option B), followed by rehosting on Windows without IIS (Option C).** A Linux port (Option D) is feasible but requires significant effort to untangle the shared library's Windows-only references.

---

## 2. Middle Tier Project/Component Map

### 2.1 Projects Participating in the Middle Tier

| Project | Role | Target Framework |
|---------|------|------------------|
| **OpenDentalServer** | Thin ASP.NET ASMX host; the IIS entry point | .NET Framework 4.8 |
| **OpenDentBusiness** | Shared business logic, DTO processing, data access | .NET Framework 4.8 |
| **CodeBase (xCodeBase)** | Shared utility library (threading, file helpers, exceptions) | .NET Framework 4.8 |
| **xCDT** | CDT dental code encryption library | .NET Framework 4.8 |
| **DataConnectionBase** | Database connection abstraction (MySQL) | .NET Framework 4.8 |

### 2.2 Key Files and Their Roles

**Hosting/Transport Layer (OpenDentalServer project):**

| File | Purpose |
|------|---------|
| `OpenDentalServer/ServiceMain.asmx` | ASMX endpoint declaration: `<%@ WebService Class="OpenDentalServer.ServiceMain" %>` |
| `OpenDentalServer/ServiceMain.asmx.cs` | Single `[WebMethod] ProcessRequest(string dtoString)` → delegates to `DtoProcessor.ProcessDto()` |
| `OpenDentalServer/Web.config` | ASP.NET config: targets 4.8, sets `authentication mode="Windows"`, `httpRuntime maxRequestLength=1GB, executionTimeout=3600s` |
| `OpenDentalServer/OpenDentalServer.csproj` | Web Application project targeting .NET 4.8 with ASP.NET project type GUID |
| `OpenDentalServer/OpenDentalServerConfig.xml` | Database connection config (MySQL: server, database, user, password) |
| `OpenDentalServer/Properties/AssemblyInfo.cs` | Standard assembly metadata |

**Core DTO Processing Layer (OpenDentBusiness/Remoting):**

| File | Purpose |
|------|---------|
| `OpenDentBusiness/Remoting/DtoProcessor.cs` | **The real server engine.** Deserializes DTOs, initializes DB connection, validates versions, processes signals, invokes business methods via reflection. |
| `OpenDentBusiness/Remoting/DataTransferObject.cs` | DTO base class + all DTO subtypes (DtoGetTable, DtoGetObject, DtoGetString, etc.) with XML serialization |
| `OpenDentBusiness/Remoting/DtoObject.cs` | Parameter wrapping/unwrapping for DTO method calls |
| `OpenDentBusiness/Remoting/Meth.cs` | Client-side reflection helper: constructs DTOs from method calls for sending to Middle Tier |
| `OpenDentBusiness/Remoting/RemotingClient.cs` | Client-side HTTP transport: sends serialized DTOs and deserializes responses |
| `OpenDentBusiness/Remoting/XmlConverter.cs` | DataTable/DataSet ↔ XML serialization |
| `OpenDentBusiness/Remoting/XmlConverterSerializer.cs` | Object ↔ XML serialization using XmlSerializer |
| `OpenDentBusiness/Remoting/WebSerializer.cs` | Utility serialization for WebServiceCustomerUpdates |
| `OpenDentBusiness/Remoting/SerializableDictionary.cs` | XML-serializable dictionary implementation |

**Service Abstraction Layer (OpenDentBusiness/WebServices):**

| File | Purpose |
|------|---------|
| `OpenDentBusiness/WebServices/IOpenDentalServer.cs` | **Key interface:** `string ProcessRequest(string dtoString)` — host-agnostic contract |
| `OpenDentBusiness/WebServices/OpenDentalServerReal.cs` | Extends auto-generated ASMX proxy + implements `IOpenDentalServer` |
| `OpenDentBusiness/WebServices/OpenDentalServerMockIIS.cs` | **IIS-free implementation** for unit testing — calls `DtoProcessor.ProcessDto()` directly |
| `OpenDentBusiness/WebServices/OpenDentalServerProxy.cs` | Factory: returns MockIIS or Real instance; configures proxy/timeout/user-agent |

### 2.3 Data Flow (Middle Tier Request)

```
Windows Client (OpenDental.exe)
  └─ Meth.GetObject(MethodBase, params)     [OpenDentBusiness/Remoting/Meth.cs]
      └─ RemotingClient.SendAndReceive(dto)  [OpenDentBusiness/Remoting/RemotingClient.cs]
          └─ IOpenDentalServer.ProcessRequest(xmlString)
              ├─ OpenDentalServerReal (ASMX SOAP call over HTTP)
              │   └─ IIS + ASP.NET pipeline
              │       └─ ServiceMain.asmx → ServiceMain.ProcessRequest()
              │           └─ DtoProcessor.ProcessDto(dtoString, serverMapPath)
              │               └─ Reflection: invoke static S-class method
              │                   └─ MySQL query via MySqlConnector
              └─ OR: OpenDentalServerMockIIS (direct in-process call)
                  └─ DtoProcessor.ProcessDto(dtoString)
```

---

## 3. Evidence of IIS/.NET Framework Coupling

### 3.1 Direct IIS/ASP.NET Dependencies

**ServiceMain.asmx.cs** — The ASMX entry point:
```csharp
// File: OpenDentalServer/ServiceMain.asmx.cs
using System.Web.Hosting;
using System.Web.Services;

[WebService(Namespace="http://www.open-dent.com/OpenDentalServer")]
[WebServiceBinding(ConformsTo=WsiProfiles.BasicProfile1_1)]
public class ServiceMain : System.Web.Services.WebService {
    [WebMethod]
    public string ProcessRequest(string dtoString) {
        return DtoProcessor.ProcessDto(dtoString, Server.MapPath("."));
    }
}
```

**IIS-specific APIs used in DtoProcessor.cs:**
```csharp
// File: OpenDentBusiness/Remoting/DtoProcessor.cs
using System.Web.Hosting;

// Line ~70: Uses HostingEnvironment.ApplicationVirtualPath for multi-site IIS deployments
if(!string.IsNullOrWhiteSpace(HostingEnvironment.ApplicationVirtualPath)
    && HostingEnvironment.ApplicationVirtualPath.Length > 1) {
    configFilePath = ODFileUtils.CombinePaths(serverMapPath,
        HostingEnvironment.ApplicationVirtualPath.Trim('/') + "Config.xml");
}

// Line ~235: Uses HttpContext.Current for verbose logging (in catch block, non-critical)
clientIP = System.Web.HttpContext.Current.Request.UserHostAddress;
```

### 3.2 .NET Framework 4.8 Hard Pin

All projects target `.NET Framework 4.8`:

```xml
<!-- OpenDentalServer.csproj -->
<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
<ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}</ProjectTypeGuids>
```

The GUID `{349c5851-65df-11da-9384-00065b846f21}` is the ASP.NET Web Application project type, which is a Visual Studio / MSBuild concept tied to the legacy project system.

```xml
<!-- OpenDentBusiness.csproj -->
<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
<LangVersion>8.0</LangVersion>
```

```xml
<!-- CodeBase/xCodeBase.csproj -->
<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
```

### 3.3 Web.config IIS Configuration

```xml
<!-- OpenDentalServer/Web.config -->
<system.web>
    <compilation debug="true" targetFramework="4.8"/>
    <authentication mode="Windows"/>
    <httpRuntime maxRequestLength="1048576" executionTimeout="3600"/>
</system.web>
<system.webServer>
    <security>
        <requestFiltering>
            <requestLimits maxAllowedContentLength="1073741824"/>
        </requestFiltering>
    </security>
</system.webServer>
```

Note: `<authentication mode="Windows"/>` is the default ASP.NET Web Application template setting. Open Dental does **not** use Windows Authentication for its own application auth — it has its own credential system (username/password in the DTO's `Credentials` object, validated in `DtoProcessor.cs` via `Userods.CheckUserAndPassword()`). This setting could likely be changed to `<authentication mode="None"/>` without impact, since the IIS Windows Authentication module is not used for Open Dental's application-level authentication. However, some deployments may rely on IIS-level Windows Authentication as a transport security layer (e.g., to restrict who can reach the endpoint at the network level), so the setting should be validated against actual deployment configurations before changing.

### 3.4 ASMX References in OpenDentalServer.csproj

```xml
<Reference Include="System.Web" />
<Reference Include="System.Web.ApplicationServices" />
<Reference Include="System.Web.DynamicData" />
<Reference Include="System.Web.Entity" />
<Reference Include="System.Web.Extensions" />
<Reference Include="System.Web.Services" />
```

These are the typical ASP.NET / ASMX web service references. Most are unused by the thin host and are default template includes.

---

## 4. Evidence of Windows-Only Coupling

### 4.1 Windows-Only Assembly References in OpenDentBusiness.csproj

These references in the **shared** `OpenDentBusiness.dll` are Windows-only:

| Reference | Category | Server-side relevance |
|-----------|----------|----------------------|
| `System.Windows.Forms` | WinForms UI | **Client-only** — but referenced from business library |
| `PresentationCore` | WPF | **Client-only** |
| `PresentationFramework` | WPF | **Client-only** |
| `WindowsBase` | WPF foundation | **Client-only** |
| `System.Xaml` | WPF/XAML | **Client-only** |
| `System.DirectoryServices` | Active Directory/LDAP | Potentially used server-side for AD authentication |
| `System.ServiceProcess` | Windows Services | **Client-only** — for managing OpenDental background services |
| `System.Management` | WMI | Low likelihood of server-side use |
| `System.EnterpriseServices` | COM+ | Legacy, likely unused |
| `System.Drawing` | GDI+ graphics | Used in Remoting/WebSerializer.cs (`System.Drawing.Imaging`) |
| `Microsoft.VisualBasic` | VB runtime | Utility (string helpers like `Strings.StrConv`) |

### 4.2 Windows-Only Pre-Built DLLs

From `OpenDentBusiness.csproj` references to `Required dlls/`:

| DLL | Issue |
|-----|-------|
| `SigPlusNET.dll` | Topaz signature pad SDK — Windows-only hardware driver (x86) |
| `Interop.Word.dll` | Microsoft Word COM interop — Windows-only |
| `Bridges.dll` | processorArchitecture=x86 — Windows hardware bridges |
| `Sparks3D.dll` | 3D tooth chart rendering — likely Windows-only |
| `RS232.dll` | Serial port access — hardware, Windows |
| `Tao.Platform.Windows.dll` | OpenGL on Windows — legacy rendering, Windows-only |
| `Dicom.Native.dll` | processorArchitecture=x86 — medical imaging, native x86 |
| `Health.Direct.*.dll` | .NET Direct Project for healthcare email |
| `VirtualWeb.dll` | processorArchitecture=MSIL but custom — unknown portability |
| `ODCrypt.dll` | Custom encryption library — unknown portability |

### 4.3 Publish Manifest (Historical)

`OpenDentalServer.Publish.xml` shows the server deployment includes:
- `SigPlusNET.dll` — signature pad hardware driver
- `Interop.Word.dll` — Word COM interop
- `Tao.Platform.Windows.dll` — Windows OpenGL
- `Tao.OpenGl.dll` — OpenGL bindings

These DLLs being in the server publish manifest likely explains the documented IIS requirement for **"Enable 32-Bit Applications = True"** — `SigPlusNET.dll` and `Bridges.dll` are x86-only native DLLs.

### 4.4 System.Drawing Usage in Remoting Layer

`OpenDentBusiness/Remoting/WebSerializer.cs` imports:
```csharp
using System.Drawing;
using System.Drawing.Imaging;
```

This is used for serializing image data in web service payloads. `System.Drawing` on .NET Framework uses GDI+ which is Windows-only. On modern .NET, `System.Drawing.Common` requires Windows or can use `libgdiplus` on Linux with limitations.

### 4.5 What's NOT Present (Positive Signals)

Searches for these Windows-only patterns returned **zero results** in the codebase:

- `Microsoft.Win32` (registry access) — **Not found**
- `DllImport` / P/Invoke — **Not found** (at least in search-indexed files)
- `WindowsIdentity` — **Not found**
- `EventLog` — **Not found**
- `HttpContext.Current` — Found only in DtoProcessor.cs verbose logging (non-critical, wrapped in try/catch)

---

## 5. Separation of Hosting Glue vs Business Logic

### 5.1 The Hosting Glue is Razor-Thin

The entire IIS/ASMX hosting layer is effectively:

**1 project (OpenDentalServer)** containing:
- `ServiceMain.asmx` — 1 line declaring the WebService class
- `ServiceMain.asmx.cs` — ~10 lines of actual code (a single `[WebMethod]` that calls `DtoProcessor.ProcessDto()`)
- `Web.config` — Standard ASP.NET config
- `OpenDentalServerConfig.xml` — Database connection settings (portable XML)

### 5.2 The IOpenDentalServer Interface Already Provides Abstraction

```csharp
// File: OpenDentBusiness/WebServices/IOpenDentalServer.cs
public interface IOpenDentalServer {
    string ProcessRequest(string dtoString);
}
```

This is already a perfect host-agnostic contract. Two implementations exist:

1. **OpenDentalServerReal** — wraps the ASMX proxy client (for ClientMT → ServerMT calls)
2. **OpenDentalServerMockIIS** — calls `DtoProcessor.ProcessDto()` directly without IIS

The MockIIS implementation **proves** the entire DTO processing pipeline works without IIS:

```csharp
// File: OpenDentBusiness/WebServices/OpenDentalServerMockIIS.cs
public class OpenDentalServerMockIIS : IOpenDentalServer {
    public string ProcessRequest(string dtoString) {
        return RunWebMethod(() => DtoProcessor.ProcessDto(dtoString));
    }
}
```

### 5.3 Business Logic Lives in Shared Libraries

The business/domain logic is in `OpenDentBusiness.dll`:
- `Data Interface/` — hundreds of "S-class" static data access classes (Patients.cs, Appointments.cs, Claims.cs, etc.)
- `Crud/` — auto-generated CRUD operations for each table type
- `TableTypes/` — entity/table type definitions
- `Logic/` — business logic modules
- `Eclaims/` — electronic claims processing
- `X12/` — EDI X12 healthcare transaction support
- `HL7/` — HL7 healthcare messaging
- `Email/` — email handling
- `Bridges/` — integrations with external systems
- `Remoting/` — the DTO/serialization framework (host-agnostic except for 2 `System.Web` calls in DtoProcessor.cs)

The `Remoting/` directory is the critical shared infrastructure that both client and server use. It is 95% host-agnostic.

### 5.4 IIS Coupling Points to Remove (Exhaustive List)

| Location | IIS API Used | Replacement |
|----------|-------------|-------------|
| `ServiceMain.asmx.cs` | `System.Web.Services.WebService` base class, `Server.MapPath(".")` | Replace with any HTTP handler that passes `AppDomain.CurrentDomain.BaseDirectory` |
| `DtoProcessor.cs` ~line 70 | `HostingEnvironment.ApplicationVirtualPath` | Pass application path from host, or use `AppDomain.CurrentDomain.BaseDirectory` |
| `DtoProcessor.cs` ~line 235 | `HttpContext.Current.Request.UserHostAddress` | Pass client IP from host layer, or use `HttpContext` abstraction |

That's it. **Three call sites.**

---

## 6. Modernization Assessment

### 6.1 Direct Answers

**Is IIS a hard dependency, or just the currently supported host?**
> **Just the currently supported host.** The IIS coupling is 3 call sites in 2 files. The project already has `IOpenDentalServer` as a host-agnostic interface and `OpenDentalServerMockIIS` proving the core works without IIS.

**Is Windows a hard dependency for the Middle Tier?**
> **Soft dependency, not hard.** The business logic library (`OpenDentBusiness.dll`) references Windows-only assemblies (WinForms, WPF, COM interop, hardware drivers), but most of these are client-side concerns that happen to live in the shared library. The actual server-side code paths likely don't call into WinForms/WPF, but the assembly references must resolve at load time, which means these DLLs must be present. `System.Drawing` is actively used in serialization code.

**Could the Middle Tier likely run in a Windows container with acceptable effort?**
> **Yes, high confidence.** IIS runs in Windows Server Core containers. The main work is packaging the ASP.NET 4.8 app with all its `Required dlls/` dependencies into a container image. No code changes required.

**Could it be rehosted on Windows without IIS?**
> **Yes, with modest effort.** Create a self-hosted HTTP server (HttpListener, OWIN, or even a .NET Framework 4.8 console app with Kestrel via Microsoft.AspNetCore.Server.Kestrel) that accepts POST requests and calls `DtoProcessor.ProcessDto()`. The `OpenDentalServerMockIIS` class is essentially a proof of concept for this.

**Could it be ported to Linux / modern .NET?**
> **Possible but significant effort.** The business library needs to be retargeted to .NET 8+ and its Windows-only references need to be dealt with (stubbed, conditionally compiled, or refactored into a separate client-only assembly). `System.Drawing` usage needs to move to a cross-platform alternative. The x86 native DLLs (SigPlusNET, Dicom.Native, Bridges) are not needed server-side but must be removed from the assembly references or made conditional.

**Is the Linux path a rehost, a port, or effectively a partial rewrite?**
> **A port.** The hosting glue is trivial to replace (days). But retargeting `OpenDentBusiness.dll` from .NET Framework 4.8 to modern .NET, resolving all Windows-only assembly references, and validating behavior is a multi-week to multi-month effort depending on the size of the dependency surface. It is NOT a rewrite — the business logic and DTO pattern are inherently portable.

### 6.2 Recommended Path

#### Option A: Stay on IIS/Windows (Status Quo)
- **Feasibility:** High
- **Effort:** None
- **Major blockers:** None
- **Top unknowns:** None
- **First PoC:** N/A
- **Assessment:** This is the safe default but doesn't improve operational flexibility.

#### Option B: Windows Containerized Middle Tier ⭐ RECOMMENDED FIRST STEP
- **Feasibility:** High
- **Effort:** Low–Medium
- **Major blockers:**
  - Windows Server Core container images are large (~5-6 GB)
  - IIS must be enabled in the container image (Windows feature)
  - Must include all `Required dlls/` and `RequiredServerDlls/` in the image
  - ASP.NET 4.8 must be installed in the container
- **Top unknowns:**
  - Whether all native x86 DLLs (SigPlusNET, Bridges.dll) load correctly in a container
  - Whether the "Enable 32-Bit Applications" IIS setting works in a container app pool
  - License implications of native DLLs in a container image
- **First PoC experiment:**
  1. Create a `Dockerfile` using `mcr.microsoft.com/dotnet/framework/aspnet:4.8-windowsservercore-ltsc2022`
  2. Copy the published OpenDentalServer site into the image
  3. Include `OpenDentalServerConfig.xml` with MySQL connection to an external database
  4. Test `POST /OpenDentalServer/ServiceMain.asmx` with a simple DTO payload

#### Option C: Rehost on Windows Without IIS
- **Feasibility:** Medium–High
- **Effort:** Medium
- **Major blockers:**
  - Must replace `Server.MapPath(".")` with file system path resolution
  - Must replace `HostingEnvironment.ApplicationVirtualPath` with configuration
  - Must replace `HttpContext.Current` in verbose logging with an alternative
  - Clients currently construct ASMX SOAP calls; the new host must speak the same wire protocol OR client must be updated
- **Top unknowns:**
  - Whether the ASMX SOAP envelope format can be replicated by a simple HTTP handler, or whether clients need to be updated to use a simpler HTTP POST
  - Performance characteristics of self-hosted vs IIS (thread pool, keep-alive, etc.)
- **First PoC experiment:**
  1. Create a .NET Framework 4.8 console app that uses `HttpListener`
  2. Accept POST to `/OpenDentalServer/ServiceMain.asmx`
  3. Extract the `dtoString` parameter from the SOAP envelope (or switch to raw HTTP POST)
  4. Call `DtoProcessor.ProcessDto(dtoString, AppDomain.CurrentDomain.BaseDirectory)`
  5. Return serialized response
  6. Test with a real Open Dental client

#### Option D: Port to Modern .NET / Linux
- **Feasibility:** Medium (with significant effort)
- **Effort:** High–Very High
- **Major blockers:**
  - `OpenDentBusiness.dll` is ~163 KB of csproj with hundreds of source files — it must be retargeted to .NET 8+
  - Windows-only references (System.Windows.Forms, PresentationFramework, WindowsBase, System.Xaml, System.DirectoryServices) must be removed, stubbed, or made conditional for server-side compilation
  - `System.Drawing` usage in `WebSerializer.cs` and potentially other files needs a cross-platform replacement (SkiaSharp, ImageSharp, or `System.Drawing.Common` with `libgdiplus`)
  - Pre-built DLLs (`Required dlls/`) may not have Linux/x64 variants
  - `CDT.dll` (encryption) must be verified for cross-platform compatibility
  - `MySqlConnector.dll` itself is cross-platform (good sign)
  - All unit tests must be re-validated
- **Top unknowns:**
  - How many business logic code paths actually invoke Windows-only APIs at runtime vs just having compile-time references
  - Whether `CDT.dll` and `ODCrypt.dll` contain native code or are pure managed
  - How deeply `System.Drawing` is used beyond `WebSerializer.cs`
  - Whether `System.DirectoryServices` is used for Active Directory authentication on the server side (would need LDAP alternative on Linux)
  - The full extent of `AllowUnsafeBlocks=true` usage — unsafe code may have platform assumptions
- **First PoC experiment:**
  1. Create a .NET 8 project that references `OpenDentBusiness` source files (not the compiled DLL)
  2. Use `#if` directives or a compatibility shim project to stub out Windows-only references
  3. Get `DtoProcessor.ProcessDto()` to compile and run against a MySQL database
  4. Create a minimal ASP.NET Core endpoint: `app.MapPost("/ServiceMain.asmx", (string dtoString) => DtoProcessor.ProcessDto(dtoString))`
  5. Test with a modified client or a raw HTTP POST

---

## 7. Appendix: Evidence Files and Search Results

### 7.1 Files Examined

| File | SHA | Key Finding |
|------|-----|-------------|
| `OpenDentalServer/ServiceMain.asmx` | `7513a25` | Single-line ASMX directive |
| `OpenDentalServer/ServiceMain.asmx.cs` | `8ffda51` | 10 lines of code, delegates to DtoProcessor |
| `OpenDentalServer/Web.config` | `f791e16` | .NET 4.8, Windows auth mode, 1GB max request |
| `OpenDentalServer/OpenDentalServer.csproj` | `2365cee` | ASP.NET Web App project type, .NET 4.8, references System.Web.* |
| `OpenDentalServer/OpenDentalServerConfig.xml` | `f7cc94d` | MySQL connection config |
| `OpenDentalServer/OpenDentalServer.Publish.xml` | `768c996` | Publish manifest including x86 native DLLs |
| `OpenDentBusiness/Remoting/DtoProcessor.cs` | `3a0bf45` | Core server engine, uses HostingEnvironment + HttpContext |
| `OpenDentBusiness/Remoting/DataTransferObject.cs` | `cb18f95` | DTO type hierarchy, XML serialization |
| `OpenDentBusiness/Remoting/Meth.cs` | `7326066` | Client-side DTO construction via reflection |
| `OpenDentBusiness/Remoting/RemotingClient.cs` | `2abe0e3` | Client HTTP transport, connection retry logic |
| `OpenDentBusiness/Remoting/WebSerializer.cs` | `0510151` | Uses System.Drawing for image serialization |
| `OpenDentBusiness/WebServices/IOpenDentalServer.cs` | `659838d` | Host-agnostic interface: `string ProcessRequest(string)` |
| `OpenDentBusiness/WebServices/OpenDentalServerMockIIS.cs` | `5133235` | **Proof that DtoProcessor works without IIS** |
| `OpenDentBusiness/WebServices/OpenDentalServerProxy.cs` | `2de33c5` | Factory for real vs mock server instances |
| `OpenDentBusiness/WebServices/OpenDentalServerReal.cs` | `a750daa` | Wraps ASMX proxy, implements IOpenDentalServer |
| `OpenDentBusiness/OpenDentBusiness.csproj` | `ace1bfe` | .NET 4.8, Windows-only refs, x86 native DLLs |
| `CodeBase/xCodeBase.csproj` | `0a08364` | .NET 4.8, WebView2 references (client-only) |
| `BUILDING.Linux` | `cbfb4d0` | Historical Linux build with Mono, build flags for Windows feature exclusion |

### 7.2 Negative Search Results (Patterns NOT Found)

| Pattern | Significance |
|---------|-------------|
| `Microsoft.Win32` | No registry access in business logic |
| `DllImport` | No P/Invoke in indexed source files |
| `WindowsIdentity` | No Windows auth in business logic |
| `EventLog` | No Windows Event Log dependency |
| `HttpContext.Current` | Found only in DtoProcessor.cs logging (non-critical) |

### 7.3 Critical Architectural Evidence

**Evidence that IIS is NOT architecturally required:**

1. `IOpenDentalServer` interface exists with a single method: `string ProcessRequest(string dtoString)`
2. `OpenDentalServerMockIIS` implements this interface and calls `DtoProcessor.ProcessDto()` directly — no IIS, no System.Web
3. `DtoProcessor.ProcessDto()` accepts `serverMapPath` as an optional string parameter with default `""` — it doesn't require `Server.MapPath()`
4. The DTO serialization uses `System.Xml.Serialization` which is cross-platform
5. Database access uses `MySqlConnector` which is cross-platform

**Evidence that Windows IS currently entangled:**

1. `OpenDentBusiness.csproj` references: System.Windows.Forms, PresentationCore, PresentationFramework, WindowsBase, System.Xaml, System.DirectoryServices
2. Pre-built DLLs with `processorArchitecture=x86`: Bridges.dll, Dicom.Native.dll, SigPlusNET.dll
3. COM interop DLLs: Interop.Word.dll
4. `System.Drawing` used in WebSerializer.cs (GDI+ is Windows-only in .NET Framework)
5. `AllowUnsafeBlocks=true` in csproj — potential platform-specific unsafe code
6. Legacy Tao.Platform.Windows.dll in publish manifest

**Evidence of prior Linux awareness:**

1. `BUILDING.Linux` file exists with Mono build instructions and flags: `LINUX`, `MONO`, `DISABLE_MICROSOFT_OFFICE`, `DISABLE_WINDOWS_BRIDGES`
2. The BUILDING.Linux file dates from 2007 and references Mono 1.2.5+ — this is historical but shows the project has considered cross-platform before

### 7.4 The "Enable 32-Bit Applications" Explanation

The documented IIS requirement for "Enable 32-Bit Applications = True" in the app pool is almost certainly caused by:

1. **SigPlusNET.dll** — Topaz signature pad SDK, `processorArchitecture=MSIL` but depends on x86 native COM components
2. **Bridges.dll** — `processorArchitecture=x86` explicitly
3. **Dicom.Native.dll** — `processorArchitecture=x86` explicitly

These DLLs are loaded because `OpenDentBusiness.dll` references them. Even if the server never uses signature pad functionality, the assembly loader may try to resolve these references, and x86 DLLs cannot load in a 64-bit process without the WoW64 compatibility layer (enabled by "Enable 32-Bit Applications = True").

---

## Summary Table

| Dimension | Assessment |
|-----------|-----------|
| IIS dependency | **Packaging only** — 3 call sites to replace |
| ASP.NET / ASMX dependency | **Packaging only** — trivially replaceable |
| .NET Framework 4.8 dependency | **Hard, but portable** — all projects target 4.8, would need retargeting for Linux |
| Windows dependency | **Soft** — business library carries client-side Windows refs |
| x86 dependency | **Packaging artifact** — native x86 DLLs not needed server-side |
| Database | **Cross-platform** — MySQL via MySqlConnector |
| Business logic portability | **High** — already in shared library, host-agnostic interface exists |
| Containerization readiness | **High** — straightforward Windows container packaging |
| Linux readiness | **Medium** — requires .NET 8 retargeting + dependency cleanup |
