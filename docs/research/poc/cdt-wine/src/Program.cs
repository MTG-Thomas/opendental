// CDT behavior probe for Wine testing. Synthetic inputs only.
// NEVER calls GetADA*/GetADAcodes (ADA-copyrighted content). Reports pass/fail + round-trips.
using System;

public static class CdtProbe {
	public static int Main() {
		Console.WriteLine("runtime=" + Environment.Version + " os=" + Environment.OSVersion);
		int failures = 0;
		// 1. The allowlist gate, called directly: does it pass from this harness location?
		try {
			CDT.Class1.VerifyCallingAssembly();
			Console.WriteLine("VerifyCallingAssembly: PASS (no throw)");
		}
		catch (Exception ex) {
			failures++;
			Console.WriteLine("VerifyCallingAssembly: THROW " + ex.GetType().FullName + ": " + ex.Message.Split('\n')[0]);
		}
		// 2. Obfuscation round-trip with synthetic plaintext (exercises ENCRYPT_KEY gate).
		try {
			string conceal = "";
			bool okE = CDT.Class1.ConcealClearinghouse("synthetic-probe-123", out conceal);
			Console.WriteLine("ConcealClearinghouse: ok=" + okE + " len=" + (conceal ?? "").Length);
			string reveal = "";
			bool okD = CDT.Class1.RevealClearinghouse(conceal, out reveal);
			Console.WriteLine("RevealClearinghouse: ok=" + okD + " match=" + (reveal == "synthetic-probe-123"));
			if (!okE || !okD || reveal != "synthetic-probe-123") {
				failures++;
			}
		}
		catch (Exception ex) {
			failures++;
			Console.WriteLine("Conceal/Reveal: THROW " + ex.GetType().FullName + ": " + ex.Message.Split('\n')[0]);
		}
		// 3. TryEncrypt/TryDecrypt round-trip (exercises ENCRYPT_KEY_V2 gate).
		try {
			string enc = CDT.Class1.TryEncrypt("synthetic-probe-456");
			string dec = CDT.Class1.TryDecrypt(enc);
			Console.WriteLine("TryEncrypt/Decrypt: match=" + (dec == "synthetic-probe-456"));
			if (dec != "synthetic-probe-456") {
				failures++;
			}
		}
		catch (Exception ex) {
			failures++;
			Console.WriteLine("TryEncrypt/Decrypt: THROW " + ex.GetType().FullName + ": " + ex.Message.Split('\n')[0]);
		}
		Console.WriteLine(failures == 0 ? "CDTPROBE_SUCCESS" : "CDTPROBE_FAILURES=" + failures);
		return failures == 0 ? 0 : 1;
	}
}
