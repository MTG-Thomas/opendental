using System.Xml;

namespace OdMtSpike;

/// <summary>
/// SOAP envelope handling for the single middle-tier route.
/// Contract source: OpenDentBusiness/Web References/OpenDentalServer/ServiceMain.wsdl
/// (document/literal, one operation: ProcessRequest(string dtoString) -> string).
/// Envelopes are built/parsed with XmlReader/XmlWriter so escaping matches what
/// ASMX/SoapHttpClientProtocol produce on .NET Framework (no hand-escaping).
/// </summary>
public static class Soap {
	/// <summary>StringWriter that declares utf-8 (ASMX writes bytes; a plain
	/// StringBuilder-backed writer would declare utf-16).</summary>
	private sealed class Utf8Writer : System.IO.StringWriter {
		public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
	}
	public const string NsSoap11 = "http://schemas.xmlsoap.org/soap/envelope/";
	public const string NsSoap12 = "http://www.w3.org/2003/05/soap-envelope";
	public const string NsOd = "http://www.open-dent.com/OpenDentalServer";
	public const string SoapAction = "http://www.open-dent.com/OpenDentalServer/ProcessRequest";

	/// <summary>
	/// Pulls the dtoString param out of a ProcessRequest envelope.
	/// Returns false + a fault reason when the body does not match the contract.
	/// XmlDocument.InnerText performs exactly one level of XML-unescaping, which is
	/// correct: the wire escaping of the dtoString element is removed here, while the
	/// DTO's own inner field escaping (XmlEscapeRecursion, applied by
	/// DataTransferObject.Serialize before the SOAP layer) stays intact for the DTO
	/// layer to undo — the same layering ASMX gives the real DtoProcessor.
	/// </summary>
	public static bool TryExtractDtoString(string envelopeXml, out string dtoString, out string fault) {
		dtoString = "";
		fault = "";
		XmlDocument doc = new();
		try {
			doc.LoadXml(envelopeXml);
		}
		catch (XmlException ex) {
			fault = "Request body is not well-formed XML: " + ex.Message;
			return false;
		}
		XmlNamespaceManager ns = new(doc.NameTable);
		ns.AddNamespace("s11", NsSoap11);
		ns.AddNamespace("s12", NsSoap12);
		ns.AddNamespace("od", NsOd);
		// SoapHttpClientProtocol always sends SOAP 1.1, but accept 1.2 bodies too.
		XmlNode? dto = doc.SelectSingleNode("/s11:Envelope/s11:Body/od:ProcessRequest/od:dtoString", ns)
			?? doc.SelectSingleNode("/s12:Envelope/s12:Body/od:ProcessRequest/od:dtoString", ns);
		if (dto is null) {
			fault = "Missing Body/ProcessRequest/dtoString in namespace " + NsOd;
			return false;
		}
		dtoString = dto.InnerText;
		return true;
	}

	/// <summary>Success envelope: the client's expected shape (ProcessRequestResponse/ProcessRequestResult).</summary>
	public static string BuildResponseEnvelope(string result) {
		// XmlWriter escapes the result string exactly as ASMX's literal serializer does
		// for a returned string (<, >, & — NOT the OD DTO-layer |/newline mappings,
		// which belong to XmlConverter.XmlEscape and are already inside result when the
		// real DtoProcessor produces it).
		Utf8Writer sb = new();
		XmlWriterSettings settings = new() { OmitXmlDeclaration = false, Encoding = System.Text.Encoding.UTF8, Indent = false };
		using (XmlWriter w = XmlWriter.Create(sb, settings)) {
			w.WriteStartDocument();
			w.WriteStartElement("soap", "Envelope", NsSoap11);
			w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
			w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
			w.WriteStartElement("Body", NsSoap11);
			w.WriteStartElement("ProcessRequestResponse", NsOd);
			w.WriteElementString("ProcessRequestResult", NsOd, result);
			w.WriteEndElement(); // ProcessRequestResponse
			w.WriteEndElement(); // Body
			w.WriteEndElement(); // Envelope
			w.WriteEndDocument();
		}
		return sb.ToString();
	}

	/// <summary>Transport-level failure envelope (HTTP 500), mirroring ASMX behavior for
	/// malformed requests. NOTE: this is distinct from app-level failures, which the real
	/// DtoProcessor returns as HTTP 200 carrying a serialized DtoException DTO.</summary>
	public static string BuildFaultEnvelope(string code, string message) {
		Utf8Writer sb = new();
		XmlWriterSettings settings = new() { OmitXmlDeclaration = false, Encoding = System.Text.Encoding.UTF8, Indent = false };
		using (XmlWriter w = XmlWriter.Create(sb, settings)) {
			w.WriteStartDocument();
			w.WriteStartElement("soap", "Envelope", NsSoap11);
			w.WriteStartElement("Body", NsSoap11);
			w.WriteStartElement("Fault", NsSoap11);
			w.WriteElementString("faultcode", NsSoap11, code);
			w.WriteElementString("faultstring", NsSoap11, message);
			w.WriteEndElement(); // Fault
			w.WriteEndElement(); // Body
			w.WriteEndElement(); // Envelope
			w.WriteEndDocument();
		}
		return sb.ToString();
	}

	/// <summary>Builds a byte-faithful sample request envelope (what SoapHttpClientProtocol sends).</summary>
	public static string BuildSampleRequest(string dtoXml) {
		Utf8Writer sb = new();
		XmlWriterSettings settings = new() { OmitXmlDeclaration = false, Encoding = System.Text.Encoding.UTF8, Indent = true };
		using (XmlWriter w = XmlWriter.Create(sb, settings)) {
			w.WriteStartDocument();
			w.WriteStartElement("soap", "Envelope", NsSoap11);
			w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
			w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
			w.WriteStartElement("Body", NsSoap11);
			w.WriteStartElement("ProcessRequest", NsOd);
			w.WriteElementString("dtoString", NsOd, dtoXml);
			w.WriteEndElement();
			w.WriteEndElement();
			w.WriteEndElement();
			w.WriteEndDocument();
		}
		return sb.ToString();
	}
}
