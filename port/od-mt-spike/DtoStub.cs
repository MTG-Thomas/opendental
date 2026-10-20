using System.Xml;

namespace OdMtSpike;

/// <summary>
/// Seam where the real DtoProcessor.ProcessDto(dtoString, mapPath) plugs in.
/// The spike ships StubDtoProcessor (shape-correct canned answers, in-memory, no DB).
/// To attempt the real pipeline, implement this interface with a thin adapter that
/// references the ported Remoting slice and register it in Program.cs — no route
/// changes needed.
/// </summary>
public interface IDtoProcessor {
	/// <param name="dtoString">DTO XML already unwrapped from the SOAP envelope (still DTO-layer escaped).</param>
	/// <param name="mapPath">Config-root directory. Shim for WebService.Server.MapPath(".") — see Program.cs.</param>
	/// <param name="clientIp">Caller IP for the verbose log. Shim for HttpContext.Current.Request.UserHostAddress.</param>
	/// <returns>Raw result string (the DTO payload), NOT a SOAP envelope. App-level
	/// failures must be returned as serialized DtoException DTO XML with HTTP 200,
	/// exactly as DtoProcessor does — never thrown past the route.</returns>
	string Process(string dtoString, string mapPath, string clientIp);
}

/// <summary>
/// Documented stub standing in for DtoProcessor.ProcessDto.
/// Dispatches on the DTO root element name (DtoGetString, DtoGetTable, ...) the same
/// way DtoProcessor's `type == typeof(DtoGetX)` chain does, and answers with
/// shape-correct synthetic payloads a real RemotingClient parser accepts:
/// "0" for void, "True"/"False" for bool, plain numbers for int/long/double,
/// XmlConverter.TableToXml-shaped XML for tables, echo text for strings.
/// Anything needing the real type graph (DtoGetObject, DtoGetDS, ...) gets a
/// serialized DtoException DTO — which is precisely what a real client turns back
/// into a thrown exception (RemotingClient.ProcessExceptionDeserialize).
/// Synthetic data only; no database; credentials are never logged.
/// </summary>
public sealed class StubDtoProcessor : IDtoProcessor {
	public string Process(string dtoString, string mapPath, string clientIp) {
		string root, method, user;
		try {
			XmlDocument doc = new();
			doc.LoadXml(dtoString);
			XmlElement el = doc.DocumentElement!;
			root = el.Name;
			method = el.SelectSingleNode("MethodName")?.InnerText ?? "";
			user = el.SelectSingleNode("Credentials/Username")?.InnerText ?? "";
		}
		catch (XmlException ex) {
			return DtoException($"DTO XML is not well-formed: {ex.Message}", "ApplicationException");
		}
		return root switch {
			// Mirrors the DtoInformation HashPassword backdoor branch (server-side hashing).
			_ when method.EndsWith("HashPassword", StringComparison.OrdinalIgnoreCase)
				=> "spike-hash:" + method,
			"DtoGetVoid" => "0",
			"DtoGetBool" => "True",
			"DtoGetInt" => "1",
			"DtoGetLong" => "1",
			"DtoGetDouble" => "1",
			// DtoProcessor returns XmlConverter.XmlEscape(strResult) for strings.
			"DtoGetString" => "spike:Method=" + method + " User=" + user,
			// DtoProcessor returns XmlConverter.TableToXml(dt): <DataTable><Name>
			// <Cols><Col> <Cells><y>pipe-joined cells. One synthetic row proves dispatch.
			"DtoGetTable" or "DtoGetTableLow" =>
				"<DataTable><Name>spike</Name><Cols><Col>Method</Col><Col>User</Col></Cols>"
				+ "<Cells><y>" + method + "|" + user + "</y></Cells></DataTable>",
			_ => DtoException($"Spike stub does not implement {root} (needs the real type graph).", "NotSupportedException"),
		};
	}

	/// <summary>
	/// Shape of DataTransferObject.Serialize(typeof(DtoException)): XmlSerializer emits
	/// the xsi/xsd namespace declarations on the root; null Credentials/Params are
	/// omitted. RemotingClient detects this shape via "&lt;DtoException xmlns:xsi=".
	/// </summary>
	public static string DtoException(string message, string type) {
		// Escape with an XML writer-equivalent (message is attacker-influenced on failures).
		string esc = System.Security.SecurityElement.Escape(message) ?? "";
		return "<DtoException xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
			+ "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
			+ "<Message>" + esc + "</Message>"
			+ "<ExceptionType>" + type + "</ExceptionType>"
			+ "<ErrorCode>0</ErrorCode></DtoException>";
	}
}
