// od-api-linux: Linux-native host for a slice of the Open Dental REST API (v1).
// Contract: vendor apispecification (resource names, ODFHIR auth, error explanations).
// Shapes: vendor apievents examples + TableTypes columns. Fresh code throughout.
//
// Endpoints (all /api/v1 except health):
//   GET /health (no auth, container/healthcheck use only - NOT part of the OD contract)
//   GET /api/v1/preferences?PrefName=X
//   GET /api/v1/patients/{patNum}
//   GET /api/v1/appointments?limit=&offset=&patNum=
// Auth: Authorization: ODFHIR {DevName}/{CustApiKey} validated against the apikey table.
//   HQ credential validation (required by OD every 6h) is STUBBED - see HqValidation below.
// Env: OD_DB_HOST/OD_DB/OD_DB_USER/OD_DB_PASS, HQ_VALIDATION=stub|enforce (default stub).
using System.Collections.Concurrent;
using System.Text.Json;
using MySqlConnector;
using OdApiLinux;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");
builder.Services.AddHostedService<EventPoller>();
var app = builder.Build();

string DbConn() {
	var csb = new MySqlConnectionStringBuilder {
		Server = Environment.GetEnvironmentVariable("OD_DB_HOST") ?? "od-mariadb",
		Port = 3306,
		Database = Environment.GetEnvironmentVariable("OD_DB") ?? "opendental",
		UserID = Environment.GetEnvironmentVariable("OD_DB_USER") ?? "root",
		Password = Environment.GetEnvironmentVariable("OD_DB_PASS") ?? "root",
		SslMode = MySqlSslMode.Disabled,
		ConvertZeroDateTime = true, // zero-dates -> DateTime.MinValue instead of throwing
	};
	return csb.ConnectionString;
}

// Enum text per vendor spec examples (ints from OpenDentBusiness/Db/Enumerations.cs) live in Shaping.
IResult Err(int code, string msg) => Results.Json(new Dictionary<string, string> { ["ErrorMessage"] = msg }, statusCode: code);

// --- HQ validation stub -----------------------------------------------------------
// OD requires Local/Service modes to validate credentials with HQ (cached 6h).
// This port performs the LOCAL apikey-table check for real and records validation time
// with the same 6h shape. enforce mode refuses (no HQ credentials in a PoC).
// A production port must implement the HQ call here before serving traffic.
ConcurrentDictionary<string, DateTime> hqValidated = new();
bool HqOk(string custKey, out string? fail) {
	fail = null;
	if ((Environment.GetEnvironmentVariable("HQ_VALIDATION") ?? "stub") == "enforce") {
		fail = "HQ credential validation not implemented in this port.";
		return false;
	}
	hqValidated[custKey] = DateTime.UtcNow; // stub: local check stands in; 6h recheck shape kept by callers if extended
	app.Logger.LogInformation("HQ validation stubbed for key {Key}", custKey[..Math.Min(4, custKey.Length)] + "***");
	return true;
}

async Task<IResult?> Authorize(HttpRequest req) {
	if (!req.Headers.TryGetValue("Authorization", out var h)) {
		return Err(401, "Missing Authorization header. Expected: ODFHIR {DevName}/{CustApiKey}.");
	}
	string[] parts = h.ToString().Split(' ', 2);
	if (parts.Length != 2 || parts[0] != "ODFHIR" || !parts[1].Contains('/')) {
		return Err(401, "Malformed Authorization header. Expected: ODFHIR {DevName}/{CustApiKey}.");
	}
	string[] keys = parts[1].Split('/', 2);
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM apikey WHERE DevName=@dev AND CustApiKey=@cust", conn);
	cmd.Parameters.AddWithValue("@dev", keys[0]);
	cmd.Parameters.AddWithValue("@cust", keys[1]);
	bool known = Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
	if (!known) {
		return Err(401, "Unknown developer/customer key pair.");
	}
	if (!HqOk(keys[1], out string? fail)) {
		return Err(503, fail!);
	}
	return null;
}

app.MapGet("/health", () => Results.Ok(new Dictionary<string, string> { ["status"] = "ok" }));

app.MapGet("/api/v1/preferences", async (HttpRequest req, string? PrefName) => {
	var auth = await Authorize(req);
	if (auth is not null) {
		return auth;
	}
	if (string.IsNullOrEmpty(PrefName)) {
		return Err(400, "PrefName query parameter is required.");
	}
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	await using var cmd = new MySqlCommand("SELECT PrefName,ValueString FROM preference WHERE PrefName=@p LIMIT 1", conn);
	cmd.Parameters.AddWithValue("@p", PrefName);
	await using var r = await cmd.ExecuteReaderAsync();
	if (!await r.ReadAsync()) {
		return Err(404, $"Preference '{PrefName}' not found.");
	}
	return Results.Ok(new Dictionary<string, object?> { ["PrefName"] = r.GetString(0), ["ValueString"] = r.GetString(1) });
});

app.MapGet("/api/v1/patients/{patNum:long}", async (HttpRequest req, long patNum) => {
	var auth = await Authorize(req);
	if (auth is not null) {
		return auth;
	}
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	await using var cmd = new MySqlCommand(
		"SELECT PatNum,LName,FName,MiddleI,Preferred,PatStatus,Gender,Position,Birthdate,SSN,Address,City,`State`,Zip,HmPhone,Email,EstBalance,PriProv,ClinicNum,Guarantor FROM patient WHERE PatNum=@p", conn);
	cmd.Parameters.AddWithValue("@p", patNum);
	await using var r = await cmd.ExecuteReaderAsync();
	if (!await r.ReadAsync()) {
		return Err(404, $"Patient {patNum} not found.");
	}
	return Results.Ok(Shaping.ShapePatient(r));
});

app.MapGet("/api/v1/appointments", async (HttpRequest req, int? limit, int? offset, long? patNum) => {
	var auth = await Authorize(req);
	if (auth is not null) {
		return auth;
	}
	int lim = Math.Min(limit ?? 100, 1000);
	int off = Math.Max(offset ?? 0, 0);
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	string sql = "SELECT AptNum,PatNum,AptStatus,Pattern,Confirmed,`Op`,Note,ProvNum,AptDateTime,ClinicNum,ProcDescript,DateTStamp FROM appointment"
		+ (patNum is not null ? " WHERE PatNum=@pat" : "") + " ORDER BY AptNum LIMIT @lim OFFSET @off";
	await using var cmd = new MySqlCommand(sql, conn);
	if (patNum is not null) {
		cmd.Parameters.AddWithValue("@pat", patNum.Value);
	}
	cmd.Parameters.AddWithValue("@lim", lim);
	cmd.Parameters.AddWithValue("@off", off);
	await using var r = await cmd.ExecuteReaderAsync();
	List<Dictionary<string, object?>> list = new();
	while (await r.ReadAsync()) {
		list.Add(Shaping.ShapeAppointment(r));
	}
	return Results.Ok(list);
});

// NOTE: payment has no DateTStamp column; SecDateTEdit (auto timestamp) is shaped as DateTStamp
// (same yyyy-MM-dd HH:mm:ss format as appointments). All other names are payment table columns.
app.MapGet("/api/v1/appointments/{aptNum:long}", async (HttpRequest req, long aptNum) => {
	var auth = await Authorize(req);
	if (auth is not null) {
		return auth;
	}
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	await using var cmd = new MySqlCommand(
		"SELECT AptNum,PatNum,AptStatus,Pattern,Confirmed,`Op`,Note,ProvNum,AptDateTime,ClinicNum,ProcDescript,DateTStamp FROM appointment WHERE AptNum=@a", conn);
	cmd.Parameters.AddWithValue("@a", aptNum);
	await using var r = await cmd.ExecuteReaderAsync();
	if (!await r.ReadAsync()) {
		return Err(404, $"Appointment {aptNum} not found.");
	}
	return Results.Ok(Shaping.ShapeAppointment(r));
});

// Write path (slice): POST creates an appointment, 201 + Location per vendor spec.
// A production port MUST enforce the OD write-path permission check here (OD checks
// Security.IsAuthorized(Permissions.AppointmentCreate / AppointmentEdit, patNum) inside the
// *ForApi insert/update path); this slice performs the apikey check only — see report.
app.MapPost("/api/v1/appointments", async (HttpRequest req) => {
	var auth = await Authorize(req);
	if (auth is not null) {
		return auth;
	}
	System.Text.Json.JsonDocument doc;
	try {
		doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
	}
	catch {
		return Err(400, "Malformed JSON body.");
	}
	using (doc) {
		var root = doc.RootElement;
		if (root.ValueKind != System.Text.Json.JsonValueKind.Object) {
			return Err(400, "Malformed JSON body: object expected.");
		}
		static bool Long(JsonElement el, string name, bool required, long def, out long val, out IResult? err) {
			val = def;
		err = null;
		if (!el.TryGetProperty(name, out var j)) {
			if (required) {
				err = Results.Json(new Dictionary<string, string> { ["ErrorMessage"] = $"{name} (number) is required." }, statusCode: 400);
			}
			return err is null && !required;
		}
		if (j.ValueKind != System.Text.Json.JsonValueKind.Number || !j.TryGetInt64(out val)) {
			err = Results.Json(new Dictionary<string, string> { ["ErrorMessage"] = $"{name} must be a number." }, statusCode: 400);
			return false;
		}
		return true;
		}
		static bool Str(JsonElement el, string name, bool required, string def, out string val, out IResult? err) {
			val = def;
		err = null;
		if (!el.TryGetProperty(name, out var j)) {
			if (required) {
				err = Results.Json(new Dictionary<string, string> { ["ErrorMessage"] = $"{name} (string) is required." }, statusCode: 400);
			}
			return err is null && !required;
		}
		if (j.ValueKind != System.Text.Json.JsonValueKind.String || j.GetString() is not string s) {
			err = Results.Json(new Dictionary<string, string> { ["ErrorMessage"] = $"{name} must be a string." }, statusCode: 400);
			return false;
		}
		val = s;
		return true;
		}
		if (!Long(root, "PatNum", true, 0, out long patNum, out var e1)) {
			return e1!;
		}
		if (!Str(root, "AptDateTime", true, "", out string aptDtRaw, out var e2)) {
			return e2!;
		}
		if (!DateTime.TryParse(aptDtRaw, out DateTime aptDt)) {
			return Err(400, "AptDateTime must be a parseable datetime (yyyy-MM-dd HH:mm:ss).");
		}
		if (!Long(root, "Op", true, 0, out long op, out var e3)) {
			return e3!;
		}
		if (!Long(root, "ProvNum", true, 0, out long provNum, out var e4)) {
			return e4!;
		}
		if (!Long(root, "ClinicNum", true, 0, out long clinicNum, out var e5)) {
			return e5!;
		}
		if (!Str(root, "Pattern", false, "", out string pattern, out var e6)) {
			return e6!;
		}
		string? note = null;
		if (root.TryGetProperty("Note", out var jNote)) {
			if (jNote.ValueKind != System.Text.Json.JsonValueKind.String) {
				return Err(400, "Note must be a string.");
			}
		note = jNote.GetString();
		}
		if (!Long(root, "Confirmed", false, 0, out long confirmed, out var e7)) {
			return e7!;
		}
		await using var conn = new MySqlConnection(DbConn());
		await conn.OpenAsync();
		await using (var chk = new MySqlCommand("SELECT COUNT(*) FROM patient WHERE PatNum=@p", conn)) {
			chk.Parameters.AddWithValue("@p", patNum);
			if (Convert.ToInt64(await chk.ExecuteScalarAsync()) == 0) {
				return Err(404, $"Patient {patNum} not found.");
			}
		}
		// Columns mirror seed.sql: NOT-NULL-no-default siblings get explicit values (0 / zero-dates),
		// all inputs parameterized. New rows are Scheduled (AptStatus=1).
		const string zero = "0001-01-01 00:00:00";
		await using var ins = new MySqlCommand(
			"INSERT INTO appointment (PatNum,AptStatus,Pattern,Confirmed,TimeLocked,`Op`,Note,ProvNum,ProvHyg,AptDateTime,NextAptNum,UnschedStatus,IsNewPatient,Assistant,ClinicNum,IsHygiene,DateTimeArrived,DateTimeSeated,DateTimeDismissed,InsPlan1,InsPlan2,DateTimeAskedToArrive,ProcDescript)"
			+ " VALUES (@pat,1,@pattern,@conf,0,@op,@note,@prov,0,@dt,0,0,0,0,@clinic,0,@z,@z,@z,0,0,@z,'')", conn);
		ins.Parameters.AddWithValue("@pat", patNum);
		ins.Parameters.AddWithValue("@pattern", pattern);
		ins.Parameters.AddWithValue("@conf", confirmed);
		ins.Parameters.AddWithValue("@op", op);
		ins.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
		ins.Parameters.AddWithValue("@prov", provNum);
		ins.Parameters.AddWithValue("@dt", Shaping.ToMysqlDateTime(aptDt));
		ins.Parameters.AddWithValue("@clinic", clinicNum);
		ins.Parameters.AddWithValue("@z", zero);
		await ins.ExecuteNonQueryAsync();
		long id = ins.LastInsertedId;
		await using var sel = new MySqlCommand(
			"SELECT AptNum,PatNum,AptStatus,Pattern,Confirmed,`Op`,Note,ProvNum,AptDateTime,ClinicNum,ProcDescript,DateTStamp FROM appointment WHERE AptNum=@a", conn);
		sel.Parameters.AddWithValue("@a", id);
		await using var r = await sel.ExecuteReaderAsync();
		await r.ReadAsync();
		return Results.Created($"/api/v1/appointments/{id}", Shaping.ShapeAppointment(r));
	}
});

// NOTE: payment has no DateTStamp column; SecDateTEdit (auto timestamp) is shaped as DateTStamp
// (same yyyy-MM-dd HH:mm:ss format as appointments). All other names are payment table columns.
app.MapGet("/api/v1/payments", async (HttpRequest req, int? limit, int? offset, long? patNum) => {
	var auth = await Authorize(req);
	if (auth is not null) {
		return auth;
	}
	int lim = Math.Min(limit ?? 100, 1000);
	int off = Math.Max(offset ?? 0, 0);
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	string sql = "SELECT PayNum,PatNum,PayDate,PayAmt,CheckNum,BankBranch,PayType,ClinicNum,SecDateTEdit FROM payment"
		+ (patNum is not null ? " WHERE PatNum=@pat" : "") + " ORDER BY PayNum LIMIT @lim OFFSET @off";
	await using var cmd = new MySqlCommand(sql, conn);
	if (patNum is not null) {
		cmd.Parameters.AddWithValue("@pat", patNum.Value);
	}
	cmd.Parameters.AddWithValue("@lim", lim);
	cmd.Parameters.AddWithValue("@off", off);
	await using var r = await cmd.ExecuteReaderAsync();
	List<Dictionary<string, object?>> list = new();
	while (await r.ReadAsync()) {
		list.Add(Shaping.ShapePayment(r));
	}
	return Results.Ok(list);
});

app.MapGet("/api/v1/payments/{payNum:long}", async (HttpRequest req, long payNum) => {
	var auth = await Authorize(req);
	if (auth is not null) {
		return auth;
	}
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	await using var cmd = new MySqlCommand(
		"SELECT PayNum,PatNum,PayDate,PayAmt,CheckNum,BankBranch,PayType,ClinicNum,SecDateTEdit FROM payment WHERE PayNum=@p", conn);
	cmd.Parameters.AddWithValue("@p", payNum);
	await using var r = await cmd.ExecuteReaderAsync();
	if (!await r.ReadAsync()) {
		return Err(404, $"Payment {payNum} not found.");
	}
	return Results.Ok(Shaping.ShapePayment(r));
});

// TEST-ONLY sink for the events poller smoke test. NOT part of the OD contract. No auth by design
// (it stands in for a subscriber endpoint); never expose beyond test networks.
ConcurrentQueue<Dictionary<string, object?>> sink = new();
app.MapPost("/__test/sink", async (HttpRequest req) => {
	using var sr = new StreamReader(req.Body);
	string body = await sr.ReadToEndAsync();
	var rec = new Dictionary<string, object?> {
		["at"] = DateTime.UtcNow.ToString("o"),
		["eventType"] = req.Headers.TryGetValue("Event-Type", out var et) ? et.ToString() : "",
		["authPresent"] = req.Headers.ContainsKey("Authorization"),
		["body"] = body,
	};
	sink.Enqueue(rec);
	while (sink.Count > 100 && sink.TryDequeue(out _)) { }
	return Results.Ok();
});
app.MapGet("/__test/sink", () => Results.Ok(sink.ToArray()));

// TEST-ONLY cleanup for the POST /api/v1/appointments smoke test. NOT part of the OD contract.
// No auth by design (mirrors /__test/sink above); deletes a synthetic row by id. Never expose
// beyond test networks, never ship in a production host.
app.MapDelete("/__test/appointments/{aptNum:long}", async (long aptNum) => {
	await using var conn = new MySqlConnection(DbConn());
	await conn.OpenAsync();
	await using var cmd = new MySqlCommand("DELETE FROM appointment WHERE AptNum=@a", conn);
	cmd.Parameters.AddWithValue("@a", aptNum);
	if (await cmd.ExecuteNonQueryAsync() == 0) {
		return Err(404, $"Appointment {aptNum} not found.");
	}
	return Results.Ok(new Dictionary<string, object?> { ["deleted"] = aptNum });
});

app.Logger.LogInformation("od-api-linux listening on :8080 (HQ_VALIDATION={Mode})", Environment.GetEnvironmentVariable("HQ_VALIDATION") ?? "stub");
app.Run();
