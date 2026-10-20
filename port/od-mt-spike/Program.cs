using System.Diagnostics;
using System.Text;
using OdMtSpike;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ---- System.Web shims (the only 3 call sites between the ASMX route and DtoProcessor) ----
// 1. ServiceMain.asmx.cs: Server.MapPath(".") -> config-root directory for
//    OpenDentalServerConfig.xml. Kestrel equivalent: an explicit directory.
//    MT_CONFIG_ROOT overrides; default AppContext.BaseDirectory (container /app).
string mapPath = Environment.GetEnvironmentVariable("MT_CONFIG_ROOT") ?? AppContext.BaseDirectory;
// 2. DtoProcessor.cs:64 HostingEnvironment.ApplicationVirtualPath (multi-DB alias
//    routing: "<Alias>Config.xml" when the virtual path is longer than "/").
//    Kestrel equivalent: MT_ALIAS env var; "" preserves the single-config default path.
string alias = Environment.GetEnvironmentVariable("MT_ALIAS") ?? "";
// (3. DtoProcessor.cs:280 HttpContext.Current.Request.UserHostAddress -> per-request
//    HttpContext.Connection.RemoteIpAddress, used below for the verbose log only.)

IDtoProcessor processor = new StubDtoProcessor(); // SEAM: swap for the real DtoProcessor adapter here.
const long MaxBodyBytes = 64L * 1024 * 1024; // documented delta: vendor allows ~1GB (imaging payloads).

app.MapGet("/health", () => Results.Json(new { status = "ok", mode = "stub", version = "24.3.63.0", alias }));

app.MapGet("/ServiceMain.asmx", (HttpRequest req) => {
	// ASMX answers GET with an HTML help page and GET ?WSDL with the contract.
	// The spike serves the contract (transcribed to ServiceMain.wsdl) and a note otherwise.
	if (req.Query.Keys.Any(k => k.Equals("wsdl", StringComparison.OrdinalIgnoreCase))) {
		string wsdl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ServiceMain.wsdl"));
		return Results.Content(wsdl, "text/xml; charset=utf-8");
	}
	return Results.Text("od-mt-spike: POST a SOAP ProcessRequest envelope here. GET ?wsdl for the contract.", "text/plain");
});

app.MapPost("/ServiceMain.asmx", async (HttpContext ctx) => {
	DateTime received = DateTime.Now;
	Stopwatch sw = Stopwatch.StartNew();
	string clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
	ctx.Request.Headers.TryGetValue("SOAPAction", out var soapAction);

	byte[] body;
	using (MemoryStream ms = new()) {
		await ctx.Request.Body.CopyToAsync(ms);
		body = ms.ToArray();
	}
	if (body.Length > MaxBodyBytes) {
		sw.Stop();
		return Results.Content(Soap.BuildFaultEnvelope("soap:Client", $"Body {body.Length} bytes exceeds spike cap {MaxBodyBytes}."),
			"text/xml; charset=utf-8", statusCode: 500);
	}
	string envelope = Encoding.UTF8.GetString(body);
	if (!Soap.TryExtractDtoString(envelope, out string dtoString, out string fault)) {
		sw.Stop();
		Console.WriteLine($"[mt] {received:HH:mm:ss} FAIL ip={clientIp} action={soapAction} bytes={body.Length} {fault}");
		return Results.Content(Soap.BuildFaultEnvelope("soap:Client", fault), "text/xml; charset=utf-8", statusCode: 500);
	}
	// The real DtoProcessor never throws past the route: app exceptions become a
	// serialized DtoException DTO with HTTP 200. The stub honors the same contract;
	// this catch is only a backstop against stub bugs (=> SOAP Fault + 500, like ASMX).
	string result;
	try {
		result = processor.Process(dtoString, mapPath, clientIp);
	}
	catch (Exception ex) {
		sw.Stop();
		Console.WriteLine($"[mt] {received:HH:mm:ss} ERROR ip={clientIp} bytes={body.Length} {ex.GetType().Name}: {ex.Message}");
		return Results.Content(Soap.BuildFaultEnvelope("soap:Server", " spike processor error: " + ex.Message),
			"text/xml; charset=utf-8", statusCode: 500);
	}
	sw.Stop();
	// Verbose line mirrors DtoProcessor.VerboseLog fields (time, ms, bytes, ip, user, method).
	Console.WriteLine($"[mt] {received:HH:mm:ss} OK ip={clientIp} action={soapAction} ms={sw.ElapsedMilliseconds} "
		+ $"in={body.Length} out~={Encoding.UTF8.GetByteCount(result)} alias={alias}");
	return Results.Content(Soap.BuildResponseEnvelope(result), "text/xml; charset=utf-8", statusCode: 200);
});

app.Run();
