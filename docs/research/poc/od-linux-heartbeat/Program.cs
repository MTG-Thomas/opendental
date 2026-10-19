// Minimal Open Dental background-service analogue for Linux PoC.
// Uses the SAME data-access stack as 24.3 GPL: MySqlConnector 2.4.0 + OD schema
// (preference/signalod/eservicesignal, all MyISAM/utf8 per db/skeema).
// Mimics two genuine OpenDentalService duties (per vendor docs):
//   1) heartbeat/signal write (cf. EServiceSignals / Signalods.SetInvalid)
//   2) aging-style pref update + alert creation (cf. "Runs aging", "Creates an alert")
using MySqlConnector;
string host = Environment.GetEnvironmentVariable("OD_DB_HOST") ?? "od-mariadb";
string db = Environment.GetEnvironmentVariable("OD_DB") ?? "opendental";
string user = Environment.GetEnvironmentVariable("OD_DB_USER") ?? "root";
string pass = Environment.GetEnvironmentVariable("OD_DB_PASS") ?? "root";
var csb = new MySqlConnectionStringBuilder {
    Server = host, Port = 3306, Database = db,
    UserID = user, Password = pass,
    SslMode = MySqlSslMode.Disabled,
};
Console.WriteLine($"connecting to {host}:3306/{db} as {user} (MySqlConnector {typeof(MySqlConnection).Assembly.GetName().Version}, runtime {Environment.Version}, OS {System.Runtime.InteropServices.RuntimeInformation.OSDescription})");
await using var conn = new MySqlConnection(csb.ConnectionString);
await conn.OpenAsync();
var serverVersion = (await new MySqlCommand("SELECT VERSION()", conn).ExecuteScalarAsync())?.ToString();
Console.WriteLine($"server version: {serverVersion}");
// 1) Heartbeat: insert eservicesignal row (ServiceCode 0 = test harness, Severity 0 = info)
await using (var cmd = new MySqlCommand(
    "INSERT INTO eservicesignal (ServiceCode,ReasonCategory,ReasonCode,Severity,Description,SigDateTime,Tag,IsProcessed) " +
    "VALUES (0,0,0,0,'Linux PoC heartbeat: od-linux-heartbeat alive',NOW(),'poc-linux',0)", conn)) {
    Console.WriteLine($"heartbeat rows: {await cmd.ExecuteNonQueryAsync()}");
}
// 2) Signal invalidation broadcast (what Signalods.SetInvalid(InvalidType.Prefs) does)
await using (var cmd = new MySqlCommand(
    "INSERT INTO signalod (DateViewing,SigDateTime,FKey,FKeyType,IType,RemoteRole,MsgValue) " +
    "VALUES ('0001-01-01',NOW(),0,'',7,0,'poc')", conn)) {
    Console.WriteLine($"signal rows: {await cmd.ExecuteNonQueryAsync()}");
}
// 3) Aging-style pref touch: upsert DateLastAging-like marker (synthetic pref, never a real OD pref name)
await using (var cmd = new MySqlCommand(
    "INSERT INTO preference (PrefName,ValueString,Comments) VALUES ('LinuxPoCLastRun',NOW(),'synthetic PoC marker')", conn)) {
    Console.WriteLine($"pref rows: {await cmd.ExecuteNonQueryAsync()}");
}
// Readback = proof of meaningful round-trip, not just process start
await using (var cmd = new MySqlCommand(
    "SELECT (SELECT COUNT(*) FROM eservicesignal WHERE Tag='poc-linux') AS hb, " +
    "(SELECT COUNT(*) FROM signalod WHERE MsgValue='poc') AS sig, " +
    "(SELECT COUNT(*) FROM preference WHERE PrefName='LinuxPoCLastRun') AS pref", conn))
await using (var r = await cmd.ExecuteReaderAsync()) {
    await r.ReadAsync();
    Console.WriteLine($"readback: heartbeat={r.GetInt64(0)} signal={r.GetInt64(1)} pref={r.GetInt64(2)}");
    if (r.GetInt64(0) < 1 || r.GetInt64(1) < 1 || r.GetInt64(2) < 1) throw new Exception("PoC readback failed");
}
Console.WriteLine("POC_SUCCESS: genuine OD-schema writes + readback on Linux");
