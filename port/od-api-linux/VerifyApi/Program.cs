// Smoke-verifies od-api-linux against the vendor REST contract (shapes from apispec/apievents docs).
// Usage: dotnet run --project VerifyApi -- http://od-api:8080
// Exit 0 = all checks pass. Auth header: ODFHIR POCDEV/POC-CUST-KEY-1 (synthetic seed).
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

if (args.Length != 1) { Console.WriteLine("usage: VerifyApi <baseUrl>"); return 2; }
string baseUrl = args[0].TrimEnd('/');
using HttpClient anon = new();
int pass = 0, fail = 0;
void Check(string name, bool cond, string detail = "") {
	if (cond) { pass++; Console.WriteLine($"PASS {name}"); }
	else { fail++; Console.WriteLine($"FAIL {name} {detail}"); }
}
async Task<(HttpStatusCode, string)> Get(string path, bool auth = true, string? badAuth = null) {
	using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
	if (badAuth is null && auth) {
		req.Headers.Authorization = new AuthenticationHeaderValue("ODFHIR", "POCDEV/POC-CUST-KEY-1");
	}
	else if (badAuth is not null) {
		req.Headers.TryAddWithoutValidation("Authorization", badAuth);
	}
	using var res = await anon.SendAsync(req);
	return (res.StatusCode, await res.Content.ReadAsStringAsync());
}

// health (no auth by design)
{
	var (code, body) = await Get("/health", auth: false);
	Check("health", code == HttpStatusCode.OK && body.Contains("ok"), $"{code} {body}");
}
// auth failures
{
	var (code, _) = await Get("/api/v1/preferences?PrefName=DateLastAging", auth: false);
	Check("auth_missing_401", code == HttpStatusCode.Unauthorized);
	var (code2, _) = await Get("/api/v1/preferences?PrefName=DateLastAging", badAuth: "Bearer xyz");
	Check("auth_malformed_401", code2 == HttpStatusCode.Unauthorized);
	var (code3, _) = await Get("/api/v1/preferences?PrefName=DateLastAging", badAuth: "ODFHIR NOPE/NOPE");
	Check("auth_unknown_401", code3 == HttpStatusCode.Unauthorized);
}
// preferences
{
	var (code, body) = await Get("/api/v1/preferences?PrefName=DateLastAging");
	using var doc = JsonDocument.Parse(body);
	Check("pref_200_value", code == HttpStatusCode.OK && doc.RootElement.GetProperty("ValueString").GetString() == "2026-10-01", $"{code} {body}");
	var (code2, _) = await Get("/api/v1/preferences?PrefName=NoSuchPref");
	Check("pref_404", code2 == HttpStatusCode.NotFound);
}
// patient (spec shapes: enum strings, yyyy-MM-dd birthdate)
{
	var (code, body) = await Get("/api/v1/patients/1");
	using var doc = JsonDocument.Parse(body);
	var el = doc.RootElement;
	Check("patient_200_shape", code == HttpStatusCode.OK
		&& el.GetProperty("LName").GetString() == "Harrison"
		&& el.GetProperty("FName").GetString() == "Bradley"
		&& el.GetProperty("Gender").GetString() == "Male"
		&& el.GetProperty("PatStatus").GetString() == "Patient"
		&& el.GetProperty("Position").GetString() == "Married"
		&& el.GetProperty("Birthdate").GetString() == "1986-11-06", $"{code} {body}");
	var (code2, _) = await Get("/api/v1/patients/999999");
	Check("patient_404", code2 == HttpStatusCode.NotFound);
}
// appointments list (paged, spec shapes)
{
	var (code, body) = await Get("/api/v1/appointments?limit=10&offset=0&patNum=1");
	using var doc = JsonDocument.Parse(body);
	var arr = doc.RootElement.EnumerateArray().ToList();
	Check("appts_200_shape", code == HttpStatusCode.OK && arr.Count == 1
		&& arr[0].GetProperty("AptStatus").GetString() == "Scheduled"
		&& arr[0].GetProperty("AptDateTime").GetString() == "2020-07-31 08:30:00"
		&& arr[0].GetProperty("ProcDescript").GetString() == "Seal, Seal", $"{code} {body}");
	var (code2, body2) = await Get("/api/v1/appointments?patNum=999999");
	Check("appts_empty_array", code2 == HttpStatusCode.OK && body2.Trim() == "[]", $"{code2} {body2}");
}
Console.WriteLine($"--- {pass} passed, {fail} failed ---");
return fail == 0 ? 0 : 1;
