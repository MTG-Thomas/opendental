namespace ODCrypt;

/// <summary>
/// Deploy-time defaults for the 4-argument Encryption overloads.
/// The original binary embeds these constants; this reimplementation intentionally does not.
/// A deployer doing interop work can populate them from their own licensed copy; until then
/// the 4-argument overloads throw an informative error. Explicit key/IV overloads are unaffected.
/// </summary>
public static class ODCryptDefaults {
	/// <summary>Hex-encoded default AES key (64 hex chars = 32 bytes), or null if unknown.</summary>
	public static string? DefaultKeyHex { get; set; }
	/// <summary>Hex-encoded default AES IV (32 hex chars = 16 bytes), or null if unknown.</summary>
	public static string? DefaultIVHex { get; set; }
}
