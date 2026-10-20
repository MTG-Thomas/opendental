using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ODCrypt;

/// <summary>Small array helpers. Trivial logic, behaviorally identical by construction.</summary>
public abstract class Arrays {
	protected Arrays() { }
	public static byte[] Append(byte[] a, byte b) { byte[] r = new byte[a.Length + 1]; a.CopyTo(r, 0); r[a.Length] = b; return r; }
	public static short[] Append(short[] a, short b) { short[] r = new short[a.Length + 1]; a.CopyTo(r, 0); r[a.Length] = b; return r; }
	public static int[] Append(int[] a, int b) { int[] r = new int[a.Length + 1]; a.CopyTo(r, 0); r[a.Length] = b; return r; }
	public static bool AreEqual(bool[] a, bool[] b) => a.SequenceEqual(b);
	public static bool AreEqual(byte[] a, byte[] b) => a.SequenceEqual(b);
	public static bool AreEqual(char[] a, char[] b) => a.SequenceEqual(b);
	public static bool AreEqual(int[] a, int[] b) => a.SequenceEqual(b);
	public static bool AreEqual(uint[] a, uint[] b) => a.SequenceEqual(b);
	[Obsolete("Use 'AreEqual' method instead")]
	public static bool AreSame(byte[] a, byte[] b) => AreEqual(a, b);
	public static byte[] Clone(byte[] data) => (byte[])data.Clone();
	public static byte[] Clone(byte[] data, byte[] existing) { byte[] r = new byte[data.Length + existing.Length]; data.CopyTo(r, 0); existing.CopyTo(r, data.Length); return r; }
	public static int[] Clone(int[] data) => (int[])data.Clone();
	public static long[] Clone(long[] data) => (long[])data.Clone();
	public static ulong[] Clone(ulong[] data) => (ulong[])data.Clone();
}

/// <summary>Randomness and comparison helpers. Random outputs are shape-compatible (length/charset class).</summary>
public static class CryptUtil {
	public static bool ConstantEquals(byte[] lhs, byte[] rhs) {
		return CryptographicOperations.FixedTimeEquals(lhs, rhs);
	}
	public static bool ConstantEquals(string lhs, string rhs) {
		return CryptographicOperations.FixedTimeEquals(
			System.Text.Encoding.UTF8.GetBytes(lhs), System.Text.Encoding.UTF8.GetBytes(rhs));
	}
	///<summary>n random bytes, base64-encoded.</summary>
	public static string GenerateSalt(int byteLength) {
		return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteLength));
	}
	///<summary>Base64 of ceil(n/8) random bytes (observed: n=10-&gt;2 bytes, n=32-&gt;4 bytes).</summary>
	public static string RandomString(int n) {
		return Convert.ToBase64String(RandomNumberGenerator.GetBytes((n + 7) / 8));
	}
	///<summary>Random value of an unmanaged type.</summary>
	public static T Random<T>() where T : unmanaged {
		byte[] bytes = RandomNumberGenerator.GetBytes(Marshal.SizeOf<T>());
		return MemoryMarshal.Read<T>(bytes);
	}
}
