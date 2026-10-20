// Asserts the clean-room ODCrypt.Portable against black-box vectors from the original.
// Usage: dotnet run --project src/Verify -- ../vectors.json
// Exit 0 = all deterministic vectors match. Random-output methods are shape-checked only.
using System.Text.Json;
using ODCrypt;

if (args.Length != 1) { Console.WriteLine("usage: Verify <vectors.json>"); return 2; }
using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(args[0]));
JsonElement v = doc.RootElement;
int pass = 0, fail = 0;
void Check(string name, bool cond, string detail = "") {
	if (cond) { pass++; Console.WriteLine($"PASS {name}"); }
	else { fail++; Console.WriteLine($"FAIL {name} {detail}"); }
}
string Get(JsonElement e, string key) => e.GetProperty(key).GetString() ?? "";
bool GetB(JsonElement e, string key) => e.GetProperty(key).GetBoolean();

string key64 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
string iv32 = "0123456789ABCDEF0123456789ABCDEF";

// --- codecs ---
Check("STB_ascii", Convert.ToBase64String(Encryption.StringToBytes("ABC", true)) == Get(v, "StringToBytes_ABC_ascii"));
Check("STB_unicode", Convert.ToBase64String(Encryption.StringToBytes("ABC", false)) == Get(v, "StringToBytes_ABC_unicode"));
Check("BTS_hex", Encryption.BytesToString(Convert.FromBase64String(Get(v, "StringToBytes_ABC_ascii")), true) == Get(v, "BytesToString_ascii_hex"));
Check("BTS_raw", Encryption.BytesToString(Convert.FromBase64String(Get(v, "StringToBytes_ABC_ascii")), false) == Get(v, "BytesToString_ascii_raw"));
Check("BTS_edge_hex", Encryption.BytesToString([0x80, 0x90, 0xFF], true) == Get(v, "BytesToString_edge_hex"));
Check("BTS_edge_raw", Encryption.BytesToString([0x80, 0x90, 0xFF], false) == Get(v, "BytesToString_edge_raw"));
Check("STB_edge_rt", Convert.ToBase64String(Encryption.StringToBytes(Encryption.BytesToString([0x80, 0x90, 0xFF], false), false)) == v.GetProperty("StringToBytes_edge_roundtrip").GetProperty("outB64").GetString());
Check("EncodeForUnicode_ascii", Encryption.EncodeForUnicode("hello test ~!") == Get(v, "EncodeForUnicode"));
Check("DecodeForUnicode_ascii", Encryption.DecodeForUnicode(Get(v, "EncodeForUnicode")) == Get(v, "DecodeForUnicode"));
Check("EncodeForUnicode_nonascii", Encryption.EncodeForUnicode("h\u00e9llo w\u00f6rld \u2713 \u2192") == Get(v, "EncodeForUnicode_nonascii"));
Check("DecodeForUnicode_nonascii", Encryption.DecodeForUnicode(Get(v, "EncodeForUnicode_nonascii")) == Get(v, "DecodeForUnicode_nonascii"));
Check("EncodeForUnicode_DEL", Encryption.EncodeForUnicode("a\u007fb") == Get(v, "EncodeForUnicode_DEL"));
Check("DecodeForUnicode_validNUL", Encryption.DecodeForUnicode("A\0" + "00e9") == Get(v, "DecodeForUnicode_validNUL"));
try {
	Encryption.DecodeForUnicode("a\0ZZZZ");
	Check("DecodeForUnicode_loneNUL_throws", false, "no throw");
}
catch (FormatException ex) {
	Check("DecodeForUnicode_loneNUL_throws", ex.Message.Contains("recognizable digits"), ex.Message);
}

// --- AES string round-trips vs original ciphertext ---
foreach (bool wf in new[] { true, false }) {
	foreach (int len in new[] { 0, 16, 32, 33 }) {
		string tag = $"web{wf}_len{len}";
		JsonElement e = v.GetProperty($"Encrypt2_{tag}");
		string failText = "";
		bool ok = Encryption.EncryptString(
			len == 0 ? "" : len == 16 ? "1234567890123456" : len == 32 ? "12345678901234567890123456789012" : "Linux PoC synthetic plaintext 123",
			wf, key64, iv32, out string enc, ref failText);
		Check($"Encrypt_{tag}", ok == GetB(e, "ok") && enc == Get(e, "encrypted"), $"ok={ok} enc={enc}");
		JsonElement d = v.GetProperty($"Decrypt2_{tag}");
		string fail2 = "";
		bool ok2 = Encryption.DecryptString(enc, wf, key64, iv32, out string plain, ref fail2);
		Check($"Decrypt_{tag}", ok2 == GetB(d, "ok") && plain == Get(d, "plain"), $"ok={ok2} plain={plain}");
	}
}
// accent plaintext: decides Latin-1 vs UTF-8 hypothesis (asserted equal, not just ok)
{
	string failText = "";
	bool ok = Encryption.EncryptString("h\u00e9llo", true, key64, iv32, out string enc, ref failText);
	Check("Encrypt_accent", ok && enc == Get(v.GetProperty("Encrypt_accent"), "encrypted"), $"ok={ok} enc={enc}");
	string fail2 = "";
	bool ok2 = Encryption.DecryptString(enc, true, key64, iv32, out string back, ref fail2);
	Check("Decrypt_accent", ok2 && back == Get(v.GetProperty("Decrypt_accent"), "plain"), back);
}
// legacy first-gen vectors (bad IV era): decrypt-empty and default-key paths
{
	string f = "";
	bool ok = Encryption.DecryptString("", true, key64, iv32, out string p, ref f);
	Check("Decrypt_empty", ok && p == "");
}
{
	string f = "";
	bool ok = Encryption.DecryptString(Get(v.GetProperty("EncryptString_defaultkey"), "encrypted"), true, out string p, ref f);
	Check("Decrypt_defaultkey_unconfigured_fails", !ok && p == "", f);
}

// --- compression ---
{
	JsonElement c = v.GetProperty("CompressString");
	string f = "";
	bool ok = Encryption.CompressString(string.Concat(Enumerable.Repeat("synthetic ", 50)), out string zipped, ref f);
	// Exact-bytes equality is NOT asserted: gzip payloads vary by compressor version across runtimes.
	// The wire property is cross-compatibility: each side decompresses the other's output.
	Console.WriteLine($"  my gzip={zipped}");
	Check("Compress_ok", ok == GetB(c, "ok"), f);
	{
		string fx = "";
		bool okx = Encryption.DecompressString(Get(c, "zipped"), out string crossPlain, ref fx);
		Check("Decompress_original_bytes", okx && crossPlain.StartsWith("synthetic synthetic "), fx);
	}
	JsonElement u = v.GetProperty("DecompressString");
	string f2 = "";
	bool ok2 = Encryption.DecompressString(zipped, out string plain, ref f2);
	Check("Decompress_roundtrip", ok2 && plain.Length == u.GetProperty("plainLen").GetInt32() && plain.StartsWith(u.GetProperty("plainPrefix").GetString() ?? ""), $"len={plain.Length}");
}

// --- hashes ---
Check("MD5", string.Equals(Convert.ToHexString(ODCrypt.MD5.Hash(Convert.FromBase64String(Get(v, "StringToBytes_ABC_ascii")))), Get(v, "MD5_Hash_abc_ascii_hex"), StringComparison.OrdinalIgnoreCase));
{
	byte[] input = Convert.FromBase64String(Get(v, "StringToBytes_ABC_unicode"));
	byte[] mine = ODCrypt.Sha3.Hash(input);
	string mineHex = Convert.ToHexString(mine);
	Check("SHA3_512", string.Equals(mineHex, Get(v, "Sha3_Hash_abc_ascii_hex"), StringComparison.OrdinalIgnoreCase), mineHex[..64]);
}

// --- misc (shape) ---
Check("SIV", Encryption.STATIC_INIT_VECTOR == Get(v, "STATIC_INIT_VECTOR"));
Check("GenKey_len", Encryption.GenerateKey().Length == v.GetProperty("GenerateKey").GetProperty("len").GetInt32());
Check("GenIV_len", Encryption.GenerateIV().Length == v.GetProperty("GenerateIV").GetProperty("len").GetInt32());
Check("GenSalt_len", CryptUtil.GenerateSalt(16).Length == v.GetProperty("GenerateSalt_16").GetProperty("len").GetInt32());
Check("ConstEq_tt", CryptUtil.ConstantEquals([1, 2, 3], [1, 2, 3]) == v.GetProperty("ConstantEquals_tt").GetBoolean());
Check("ConstEq_tf", CryptUtil.ConstantEquals([1, 2, 3], [1, 2, 4]) == v.GetProperty("ConstantEquals_tf").GetBoolean());
Check("RandomString_shape", CryptUtil.RandomString(10).Length == v.GetProperty("RandomString_10_len").GetInt32());
Check("Arrays", ODCrypt.Arrays.AreEqual(new byte[] { 1, 2, 3 }, ODCrypt.Arrays.Clone(new byte[] { 1, 2, 3 })) == v.GetProperty("Arrays_clone_eq").GetBoolean());

Console.WriteLine($"--- {pass} passed, {fail} failed ---");
// --- seeded fuzz differential ---
{
	int i = 0;
	foreach (JsonElement fz in v.GetProperty("Fuzz_encrypt").EnumerateArray()) {
		i++;
		string failText = "";
		bool ok = Encryption.EncryptString(Get(fz, "pt"), fz.GetProperty("wf").GetBoolean(), Get(fz, "key"), Get(fz, "iv"), out string enc, ref failText);
		bool same = ok == fz.GetProperty("ok").GetBoolean() && enc == Get(fz, "enc");
		if (same && ok) {
			string f2 = "";
		bool ok2 = Encryption.DecryptString(enc, fz.GetProperty("wf").GetBoolean(), Get(fz, "key"), Get(fz, "iv"), out string back, ref f2);
			same = ok2 && back == Get(fz, "pt");
		}
		Check($"Fuzz_enc[{i}]", same, $"ok={ok}");
	}
	foreach (JsonElement fh in v.GetProperty("Fuzz_hash").EnumerateArray()) {
		i++;
		byte[] input = Convert.FromBase64String(Get(fh, "input"));
		bool m = string.Equals(Convert.ToHexString(ODCrypt.MD5.Hash(input)), Get(fh, "md5"), StringComparison.OrdinalIgnoreCase);
		bool s = string.Equals(Convert.ToHexString(ODCrypt.Sha3.Hash(input)), Get(fh, "sha3"), StringComparison.OrdinalIgnoreCase);
		Check($"Fuzz_hash[{i}]", m && s);
	}
	foreach (JsonElement fu in v.GetProperty("Fuzz_unicode").EnumerateArray()) {
		i++;
		string encU = Encryption.EncodeForUnicode(Get(fu, "plain"));
		bool same = encU == Get(fu, "encoded");
		if (same) {
			bool wantOk = fu.GetProperty("decodeOk").GetBoolean();
			try {
				string back = Encryption.DecodeForUnicode(encU);
				same = wantOk && back == Get(fu, "decoded");
			}
			catch (FormatException) {
				same = !wantOk;
			}
		}
		Check($"Fuzz_uni[{i}]", same);
	}
}

Console.WriteLine($"--- {pass} passed, {fail} failed (incl. fuzz) ---");
return fail == 0 ? 0 : 1;
