using System.Security.Cryptography;

namespace ODCrypt;

/// <summary>MD5 helper. Standard MD5; verified byte-for-byte against original vectors.</summary>
public class MD5 {
	public static byte[] Hash(byte[] asciiBytes) {
		return System.Security.Cryptography.MD5.HashData(asciiBytes);
	}
}

/// <summary>
/// SHA3-512 helper. Verified byte-for-byte against original vectors: the original's
/// output matches NIST SHA3-512 exactly, so the framework built-in is used.
/// (A Keccak-original-padding variant was also tested and falsified by the vectors.)
/// </summary>
public class Sha3 {
	public static byte[] Hash(byte[] unicodeBytes) {
		return SHA3_512.HashData(unicodeBytes);
	}
}
