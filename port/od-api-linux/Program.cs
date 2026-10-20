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
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");
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

// Enum text per vendor spec examples (ints from OpenDentBusiness/Db/Enumerations.cs).
string[] ApptStatusText = ["None", "Scheduled", "Complete", "UnschedList", "ASAP", "Broken", "Planned", "PtNote", "PtNoteCompleted"];
string[] GenderText = ["Male", "Female", "Unknown", "Other"];
string[] PatStatusText = ["Patient", "Non-patient", "Inactive", "Archived", "Deceased", "NonPatient"];
string[] PositionText = ["Single", "Married", "Child", "Widowed", "Divorced"];
string EnumText(string[] map, object val) {
	int i = Convert.ToInt32(val);
	return (i >= 0 && i < map.Length) ? map[i] : i.ToString();
}
string Dt(object v, bool dateOnly = false) {
	if (v is not DateTime dt) {
		return "";
	}
	return dateOnly ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss");
}
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
	return Results.Ok(new Dictionary<string, object?> {
		["PatNum"] = r.GetInt64(0), ["LName"] = r.GetString(1), ["FName"] = r.GetString(2),
		["MiddleI"] = r.GetString(3), ["Preferred"] = r.GetString(4),
		["PatStatus"] = EnumText(PatStatusText, r.GetValue(5)), ["Gender"] = EnumText(GenderText, r.GetValue(6)),
		["Position"] = EnumText(PositionText, r.GetValue(7)), ["Birthdate"] = Dt(r.GetValue(8), dateOnly: true),
		["SSN"] = r.GetString(9), ["Address"] = r.GetString(10), ["City"] = r.GetString(11),
		["State"] = r.GetString(12), ["Zip"] = r.GetString(13), ["HmPhone"] = r.GetString(14),
		["Email"] = r.GetString(15), ["EstBalance"] = r.GetDouble(16), ["PriProv"] = r.GetInt64(17),
		["ClinicNum"] = r.GetInt64(18), ["Guarantor"] = r.GetInt64(19),
	});
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
		list.Add(new Dictionary<string, object?> {
			["AptNum"] = r.GetInt64(0), ["PatNum"] = r.GetInt64(1),
			["AptStatus"] = EnumText(ApptStatusText, r.GetValue(2)), ["Pattern"] = r.GetString(3),
			["Confirmed"] = r.GetInt64(4), ["Op"] = r.GetInt64(5),
			["Note"] = r.IsDBNull(6) ? "" : r.GetString(6), ["ProvNum"] = r.GetInt64(7),
			["AptDateTime"] = Dt(r.GetValue(8)), ["ClinicNum"] = r.GetInt64(9),
			["ProcDescript"] = r.IsDBNull(10) ? "" : r.GetString(10), ["DateTStamp"] = Dt(r.GetValue(11)),
		});
	}
	return Results.Ok(list);
});

app.Logger.LogInformation("od-api-linux listening on :8080 (HQ_VALIDATION={Mode})", Environment.GetEnvironmentVariable("HQ_VALIDATION") ?? "stub");
app.Run();
