using MySqlConnector;

namespace OdApiLinux;

/// <summary>Row shaping into vendor-spec JSON (enum strings, date formats). Shared by endpoints + poller.</summary>
public static class Shaping {
	private static readonly string[] ApptStatusText = ["None", "Scheduled", "Complete", "UnschedList", "ASAP", "Broken", "Planned", "PtNote", "PtNoteCompleted"];
	private static readonly string[] GenderText = ["Male", "Female", "Unknown", "Other"];
	private static readonly string[] PatStatusText = ["Patient", "Non-patient", "Inactive", "Archived", "Deceased", "NonPatient"];
	private static readonly string[] PositionText = ["Single", "Married", "Child", "Widowed", "Divorced"];

	public static string EnumText(string[] map, object val) {
		int i = Convert.ToInt32(val);
		return (i >= 0 && i < map.Length) ? map[i] : i.ToString();
	}

	public static string Dt(object v, bool dateOnly = false) {
		if (v is not DateTime dt) {
			return "";
		}
		return dateOnly ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss");
	}

	/// <summary>Naive DATETIME columns compare as formatted strings; UTC assumed (containers run UTC).</summary>
	public static string ToMysqlDateTime(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss");

	public static Dictionary<string, object?> ShapePatient(MySqlDataReader r) => new() {
		["PatNum"] = r.GetInt64(0), ["LName"] = r.GetString(1), ["FName"] = r.GetString(2),
		["MiddleI"] = r.GetString(3), ["Preferred"] = r.GetString(4),
		["PatStatus"] = EnumText(PatStatusText, r.GetValue(5)), ["Gender"] = EnumText(GenderText, r.GetValue(6)),
		["Position"] = EnumText(PositionText, r.GetValue(7)), ["Birthdate"] = Dt(r.GetValue(8), dateOnly: true),
		["SSN"] = r.GetString(9), ["Address"] = r.GetString(10), ["City"] = r.GetString(11),
		["State"] = r.GetString(12), ["Zip"] = r.GetString(13), ["HmPhone"] = r.GetString(14),
		["Email"] = r.GetString(15), ["EstBalance"] = r.GetDouble(16), ["PriProv"] = r.GetInt64(17),
		["ClinicNum"] = r.GetInt64(18), ["Guarantor"] = r.GetInt64(19),
	};

	public static Dictionary<string, object?> ShapeAppointment(MySqlDataReader r) => new() {
		["AptNum"] = r.GetInt64(0), ["PatNum"] = r.GetInt64(1),
		["AptStatus"] = EnumText(ApptStatusText, r.GetValue(2)), ["Pattern"] = r.GetString(3),
		["Confirmed"] = r.GetInt64(4), ["Op"] = r.GetInt64(5),
		["Note"] = r.IsDBNull(6) ? "" : r.GetString(6), ["ProvNum"] = r.GetInt64(7),
		["AptDateTime"] = Dt(r.GetValue(8)), ["ClinicNum"] = r.GetInt64(9),
		["ProcDescript"] = r.IsDBNull(10) ? "" : r.GetString(10), ["DateTStamp"] = Dt(r.GetValue(11)),
	};
}
