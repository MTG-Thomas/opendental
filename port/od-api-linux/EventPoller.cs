using System.Collections.Concurrent;
using MySqlConnector;

namespace OdApiLinux;

/// <summary>
/// Database-events poller per the vendor API Events spec: for each subscription with a supported
/// WatchTable, POST rows changed since DateTimeStart to EndPointUrl every PollingSeconds.
/// DateTimeStart advances only on successful delivery; backfill clamped to 3 days.
/// Supported WatchTables in this slice: Appointment, Patient (both in 24.3).
/// </summary>
public class EventPoller : BackgroundService {
	private readonly IConfiguration _config;
	private readonly ILogger<EventPoller> _log;
	private readonly ConcurrentDictionary<long, DateTime> _lastPoll = new();
	private static readonly HashSet<string> Tables = new(StringComparer.OrdinalIgnoreCase) { "Appointment", "Patient" };

	public EventPoller(IConfiguration config, ILogger<EventPoller> log) {
		_config = config;
		_log = log;
	}

	private string DbConn() {
		var csb = new MySqlConnectionStringBuilder {
			Server = Environment.GetEnvironmentVariable("OD_DB_HOST") ?? "od-mariadb",
			Port = 3306,
			Database = Environment.GetEnvironmentVariable("OD_DB") ?? "opendental",
			UserID = Environment.GetEnvironmentVariable("OD_DB_USER") ?? "root",
			Password = Environment.GetEnvironmentVariable("OD_DB_PASS") ?? "root",
			SslMode = MySqlSslMode.Disabled,
			ConvertZeroDateTime = true,
		};
		return csb.ConnectionString;
	}

	protected override async Task ExecuteAsync(CancellationToken stopping) {
		using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
		while (await timer.WaitForNextTickAsync(stopping)) {
			try {
				await PollOnce(DateTime.UtcNow, stopping);
			}
			catch (Exception ex) {
				_log.LogWarning(ex, "event poll cycle failed");
			}
		}
	}

	private async Task PollOnce(DateTime nowUtc, CancellationToken ct) {
		List<Subscription> subs = new();
		await using (var conn = new MySqlConnection(DbConn())) {
			await conn.OpenAsync(ct);
			await using var cmd = new MySqlCommand(
				"SELECT ApiSubscriptionNum,EndPointUrl,CustomerKey,WatchTable,PollingSeconds,DateTimeStart,DateTimeStop FROM apisubscription WHERE WatchTable<>'' AND (UiEventType IS NULL OR UiEventType='')", conn);
			await using var r = await cmd.ExecuteReaderAsync(ct);
			while (await r.ReadAsync(ct)) {
				subs.Add(new Subscription(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
					r.GetDateTime(5), r.GetDateTime(6)));
			}
		}
		foreach (var s in subs) {
			if (!Tables.Contains(s.WatchTable)) {
				continue;
			}
			if (s.DateTimeStop.Year > 1 && DateTime.UtcNow > s.DateTimeStop.ToUniversalTime()) {
				continue; // expired subscription (assumed semantics, documented)
			}
			DateTime last = _lastPoll.GetOrAdd(s.Num, DateTime.MinValue);
			if ((nowUtc - last).TotalSeconds < Math.Max(s.PollingSeconds, 1)) {
				continue;
			}
			_lastPoll[s.Num] = nowUtc;
			await FireSubscription(s, ct);
		}
	}

	private async Task FireSubscription(Subscription s, CancellationToken ct) {
		List<Dictionary<string, object?>> rows = new();
		DateTime pollTime = DateTime.UtcNow;
		await using (var conn = new MySqlConnection(DbConn())) {
			await conn.OpenAsync(ct);
			// Table name is whitelisted above; never interpolated unchecked.
			string table = s.WatchTable.Equals("Appointment", StringComparison.OrdinalIgnoreCase) ? "appointment" : "patient";
			string cols = table == "appointment"
				? "AptNum,PatNum,AptStatus,Pattern,Confirmed,`Op`,Note,ProvNum,AptDateTime,ClinicNum,ProcDescript,DateTStamp"
				: "PatNum,LName,FName,MiddleI,Preferred,PatStatus,Gender,Position,Birthdate,SSN,Address,City,`State`,Zip,HmPhone,Email,EstBalance,PriProv,ClinicNum,Guarantor";
			await using var cmd = new MySqlCommand(
				$"SELECT {cols} FROM {table} WHERE DateTStamp > @start AND DateTStamp >= UTC_TIMESTAMP() - INTERVAL 3 DAY ORDER BY DateTStamp LIMIT 1000", conn);
			cmd.Parameters.AddWithValue("@start", Shaping.ToMysqlDateTime(s.DateTimeStart));
			await using var r = await cmd.ExecuteReaderAsync(ct);
			while (await r.ReadAsync(ct)) {
				rows.Add(table == "appointment" ? Shaping.ShapeAppointment(r) : Shaping.ShapePatient(r));
			}
		}
		if (rows.Count == 0) {
			return;
		}
		bool delivered = await PostWebhook(s, rows, ct);
		if (delivered) {
			await using var conn = new MySqlConnection(DbConn());
			await conn.OpenAsync(ct);
			await using var cmd = new MySqlCommand("UPDATE apisubscription SET DateTimeStart=@now WHERE ApiSubscriptionNum=@n", conn);
			cmd.Parameters.AddWithValue("@now", pollTime.ToString("yyyy-MM-dd HH:mm:ss"));
			cmd.Parameters.AddWithValue("@n", s.Num);
			await cmd.ExecuteNonQueryAsync(ct);
			_log.LogInformation("events delivered: sub={Sub} table={Table} rows={Rows}", s.Num, s.WatchTable, rows.Count);
		}
		else {
			_log.LogWarning("event delivery failed (DateTimeStart NOT advanced): sub={Sub} url={Url}", s.Num, s.EndPointUrl);
		}
	}

	private static readonly HttpClient Http = new();

	private async Task<bool> PostWebhook(Subscription s, List<Dictionary<string, object?>> rows, CancellationToken ct) {
		try {
			using var req = new HttpRequestMessage(HttpMethod.Post, s.EndPointUrl);
			req.Headers.TryAddWithoutValidation("Authorization", s.CustomerKey);
			req.Headers.TryAddWithoutValidation("Workstation", Environment.MachineName);
			req.Headers.TryAddWithoutValidation("Event-Type", $"WatchTable: {s.WatchTable}");
			req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(rows),
				System.Text.Encoding.UTF8, "application/json");
			using var res = await Http.SendAsync(req, ct);
			return ((int)res.StatusCode) < 300;
		}
		catch (Exception ex) {
			_log.LogWarning(ex, "webhook POST failed");
			return false;
		}
	}

	private sealed record Subscription(long Num, string EndPointUrl, string CustomerKey, string WatchTable, int PollingSeconds, DateTime DateTimeStart, DateTime DateTimeStop);
}
