using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ODCrypt;

/// <summary>
/// Wire-compatible reimplementation of ODCrypt.Encryption as observed via black-box vectors.
/// Facts established by vectors (see vectors.json): AES-CBC/PKCS7 with hex-encoded key/IV,
/// hex output when makeWebFriendly, raw Latin-1 byte-string otherwise, Latin-1 plaintext codec,
/// NUL+4-hex unicode escaping, gzip+base64 compression. Written from scratch; no original source used.
/// </summary>
public class Encryption {
	/// <summary>32 ASCII '0' chars: the original's well-known static IV (hex for 16 zero bytes).</summary>
	public static string STATIC_INIT_VECTOR => new('0', 32);

	private static byte[] HexToBytes(string hex) {
		try {
			byte[] bytes = new byte[hex.Length / 2]; // trailing nibble dropped, matching observed 'ABC'->[0xAB]
			for (int i = 0; i < bytes.Length; i++) {
				bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
			}
			return bytes;
		}
		catch (FormatException) {
			// .NET Framework's parse message; modern runtimes reworded it. The wire behavior (throw) is what matters.
			throw new FormatException("Could not find any recognizable digits.");
		}
	}

	/// <summary>Plaintext codec hypothesis under test: UTF-8 (Latin-1 falsified by accent vector).</summary>
	private static readonly Encoding PlainCodec = Encoding.UTF8;

	private static string BytesToHex(byte[] bytes) {
		StringBuilder sb = new(bytes.Length * 2);
		foreach (byte b in bytes) {
			sb.Append(b.ToString("X2"));
		}
		return sb.ToString();
	}

	private static byte[] AesEncrypt(byte[] plain, string keyHex, string ivHex) {
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;
		aes.Key = HexToBytes(keyHex);
		aes.IV = HexToBytes(ivHex);
		return aes.EncryptCbc(plain, aes.IV);
	}

	private static byte[] AesDecrypt(byte[] cipher, string keyHex, string ivHex) {
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;
		aes.Key = HexToBytes(keyHex);
		aes.IV = HexToBytes(ivHex);
		return aes.DecryptCbc(cipher, aes.IV);
	}

	///<summary>Hex-decodes pairs ('ABC'-&gt;[0xAB]); false branch returns Latin-1 bytes of the text.</summary>
	public static byte[] StringToBytes(string asString, bool inputIs7BitAscii) {
		if (inputIs7BitAscii) {
			return HexToBytes(asString);
		}
		return Encoding.Latin1.GetBytes(asString);
	}

	///<summary>True: uppercase hex; false: raw Latin-1 byte string.</summary>
	public static string BytesToString(byte[] asBytes, bool outputAsHex) {
		if (outputAsHex) {
			return BytesToHex(asBytes);
		}
		return Encoding.Latin1.GetString(asBytes);
	}

	///<summary>Non-ASCII chars become NUL + 4 lowercase hex digits; ASCII passes through.</summary>
	public static string EncodeForUnicode(string plainText) {
		StringBuilder sb = new(plainText.Length);
		foreach (char c in plainText) {
			if (c > 127) {
				sb.Append('\0');
				sb.Append(((int)c).ToString("x4"));
			}
			else {
				sb.Append(c);
			}
		}
		return sb.ToString();
	}

	///<summary>Inverse of EncodeForUnicode. Lone NUL + non-hex throws, matching observed behavior.</summary>
	public static string DecodeForUnicode(string encoded) {
		StringBuilder sb = new(encoded.Length);
		for (int i = 0; i < encoded.Length; i++) {
			if (encoded[i] == '\0') {
				string hex = encoded.Substring(i + 1, 4);
				try {
					sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
				}
			catch (FormatException) {
					throw new FormatException("Could not find any recognizable digits.");
				}
				i += 4;
			}
			else {
				sb.Append(encoded[i]);
			}
		}
		return sb.ToString();
	}

	///<summary>32 random bytes, hex-encoded (64 chars).</summary>
	public static string GenerateKey() {
		return BytesToHex(RandomNumberGenerator.GetBytes(32));
	}

	///<summary>16 random bytes, hex-encoded (32 chars).</summary>
	public static string GenerateIV() {
		return BytesToHex(RandomNumberGenerator.GetBytes(16));
	}

	public static bool EncryptBytes(byte[] byteArray, bool makeWebFriendly, string key, string initVector, out string encrypted, ref string failText) {
		try {
			failText = "";
			byte[] cipher = AesEncrypt(byteArray, key, initVector);
			encrypted = makeWebFriendly ? BytesToHex(cipher) : Encoding.Latin1.GetString(cipher);
			return true;
		}
		catch (Exception ex) {
			encrypted = "";
			failText = ex.Message;
			return false;
		}
	}

	public static bool EncryptString(string plainText, bool makeWebFriendly, string key, string initVector, out string encrypted, ref string failText) {
		if (plainText == "") {
			encrypted = "";
			failText = "";
			return true;
		}
		return EncryptBytes(PlainCodec.GetBytes(plainText), makeWebFriendly, key, initVector, out encrypted, ref failText);
	}

	public static bool EncryptString(string plainText, bool makeWebFriendly, out string encrypted, ref string failText) {
		if (ODCryptDefaults.DefaultKeyHex is null || ODCryptDefaults.DefaultIVHex is null) {
			encrypted = "";
			failText = "No default key configured. Set ODCryptDefaults.DefaultKeyHex/DefaultIVHex from your licensed copy for HQ interop paths.";
			return false;
		}
		return EncryptString(plainText, makeWebFriendly, ODCryptDefaults.DefaultKeyHex, ODCryptDefaults.DefaultIVHex, out encrypted, ref failText);
	}

	public static bool DecryptString(string encrypted, bool isWebFriendly, string key, string initVector, out byte[] bytes, ref string failText) {
		try {
			failText = "";
			if (encrypted == "") {
				bytes = [];
				return true;
			}
			byte[] cipher = isWebFriendly ? HexToBytes(encrypted) : Encoding.Latin1.GetBytes(encrypted);
			bytes = AesDecrypt(cipher, key, initVector);
			return true;
		}
		catch (Exception ex) {
			bytes = [];
			failText = ex.Message;
			return false;
		}
	}

	public static bool DecryptString(string encrypted, bool isWebFriendly, string key, string initVector, out string plainText, ref string failText) {
		bool ok = DecryptString(encrypted, isWebFriendly, key, initVector, out byte[] bytes, ref failText);
		plainText = ok ? PlainCodec.GetString(bytes) : "";
		return ok;
	}

	public static bool DecryptString(string encrypted, bool isWebFriendly, out string plainText, ref string failText) {
		if (ODCryptDefaults.DefaultKeyHex is null || ODCryptDefaults.DefaultIVHex is null) {
			plainText = "";
			failText = "No default key configured. Set ODCryptDefaults.DefaultKeyHex/DefaultIVHex from your licensed copy for HQ interop paths.";
			return false;
		}
		return DecryptString(encrypted, isWebFriendly, ODCryptDefaults.DefaultKeyHex, ODCryptDefaults.DefaultIVHex, out plainText, ref failText);
	}

	public static bool CompressString(string unzipped, out string zipped, ref string failText) {
		try {
			failText = "";
			byte[] raw = Encoding.UTF8.GetBytes(unzipped);
			using MemoryStream ms = new();
			using (System.IO.Compression.GZipStream gz = new(ms, System.IO.Compression.CompressionLevel.Optimal)) {
				gz.Write(raw, 0, raw.Length);
			}
			zipped = Convert.ToBase64String(ms.ToArray());
			return true;
		}
		catch (Exception ex) {
			zipped = "";
			failText = ex.Message;
			return false;
		}
	}

	public static bool DecompressString(string zipped, out string plainText, ref string failText) {
		try {
			failText = "";
			byte[] raw = Convert.FromBase64String(zipped);
			using MemoryStream ms = new(raw);
			using System.IO.Compression.GZipStream gz = new(ms, System.IO.Compression.CompressionMode.Decompress);
			using MemoryStream outMs = new();
			gz.CopyTo(outMs);
			plainText = Encoding.UTF8.GetString(outMs.ToArray());
			return true;
		}
		catch (Exception ex) {
			plainText = "";
			failText = ex.Message;
			return false;
		}
	}

	// File helpers: thin wrappers, same core. No original vectors exist (unreferenced in-tree); marked accordingly.
	public static bool EncryptFile(string originalFileName, string encryptedFileName, string key, string initVector, ref string failText) {
		try {
			failText = "";
			byte[] raw = File.ReadAllBytes(originalFileName);
			if (!EncryptBytes(raw, true, key, initVector, out string encrypted, ref failText)) {
				return false;
			}
			File.WriteAllText(encryptedFileName, encrypted);
			return true;
		}
		catch (Exception ex) {
			failText = ex.Message;
			return false;
		}
	}

	public static bool DecryptFile(string encryptedFileName, string decryptedFileName, string key, string initVector, ref string failText) {
		try {
			failText = "";
			string encrypted = File.ReadAllText(encryptedFileName);
			if (!DecryptString(encrypted, true, key, initVector, out byte[] bytes, ref failText)) {
				return false;
			}
			File.WriteAllBytes(decryptedFileName, bytes);
			return true;
		}
		catch (Exception ex) {
			failText = ex.Message;
			return false;
		}
	}
}
