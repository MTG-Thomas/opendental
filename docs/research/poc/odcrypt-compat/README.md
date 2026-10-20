# ODCrypt.Portable — wire-compatible clean-room reimplementation

Target: `ODCrypt.dll` 24.3.63.0 (committed blob, no source). Method: black-box test vectors
generated from the original under .NET Framework (`tools/make_vectors.ps1`), fresh implementation
in `src/ODCrypt.Portable` (net8.0, zero dependencies), verified by `src/Verify` on Windows **and** Linux.

**Status: 47/47 deterministic vectors pass on both runtimes** (plus shape checks for random outputs).

## Recovered wire facts (all pinned by vectors, see vectors.json)

| Behavior | Original | Portable |
|---|---|---|
| AES core | AES-CBC/PKCS7, hex-encoded key (16/24/32 B) + IV (16 B) | `Aes.Create()`, same |
| `makeWebFriendly=true` output | Uppercase hex | Same |
| `makeWebFriendly=false` output | Raw Latin-1 byte string | `Encoding.Latin1`, same |
| AES plaintext codec | UTF-8 (Latin-1 falsified by accent vector) | `Encoding.UTF8` |
| `StringToBytes(s,true)` | Hex-decode pairs, trailing nibble dropped | Same (`Convert.ToByte(..,16)`) |
| `StringToBytes(s,false)` | Latin-1 bytes | Same |
| `BytesToString(b,true/false)` | Uppercase hex / Latin-1 string | Same |
| `EncodeForUnicode` | `c>127` → NUL + 4 lowercase hex; ASCII (incl. DEL) passthrough | Same |
| `DecodeForUnicode` | NUL+4hex → char; lone NUL throws Framework-text `FormatException` | Same (message reproduced; it's BCL text, not blob expression) |
| Empty encrypt/decrypt | `ok` + empty (both modes) | Same (short-circuit) |
| `CompressString` | gzip + base64 (payload bytes differ by compressor version) | gzip + base64; **cross-decompress verified both directions** |
| `MD5.Hash` | Standard MD5 | `MD5.HashData` |
| `Sha3.Hash` | **NIST SHA3-512** (Keccak-original-padding falsified by vectors) | `SHA3_512.HashData` (.NET 8 built-in) |
| `STATIC_INIT_VECTOR` | 32×`'0'` | Same constant |
| `GenerateKey/IV` | 64/32 hex chars (32/16 random bytes) | Same shape |
| `GenerateSalt(n)` | base64 of n random bytes | Same shape |
| `RandomString(n)` | base64 of ceil(n/8) random bytes (session-token shape) | Same shape |
| `ConstantEquals` | Constant-time compare | `FixedTimeEquals` |
| Bad key/IV | `ok=false`, fail text from BCL (`...Parameter name: key/iv`) | Same (BCL text can vary cosmetically by runtime; outputs + flags are the contract) |

## Known gaps (explicit, not silent)

1. **Default-key 4-arg overloads** (`EncryptString(plain,webFriendly,out,ref)` as used by
   `WebServiceMainHQProxy` registration): the original embeds its key/IV constants. This port
   routes them through `ODCryptDefaults.DefaultKeyHex/DefaultIVHex` (null → informative failure).
   Deployers doing HQ interop populate these from their own licensed copy. All explicit-key
   overloads are fully wire-compatible.
2. **`EncryptFile/DecryptFile`**: implemented as thin wrappers over the verified core, but no
   original vectors exist (unreferenced in-tree) — marked unverified.
3. **Fail-text wording**: derived from BCL exception messages, which runtimes reword occasionally.
   Exact-match asserted only where pinned; `ok` flags + outputs are asserted everywhere.

## Reproduce

```powershell
# 1. Vectors from YOUR copy of the original (Windows PowerShell 5.1, Framework CLR):
powershell -NoProfile -ExecutionPolicy Bypass -File tools/make_vectors.ps1
# 2. Verify on Windows:
dotnet run --project src/Verify -- vectors.json
# 3. Verify on Linux (hermetic):
docker build -t odcrypt-verify .
docker run --rm odcrypt-verify
# expect: 47 passed, 0 failed (plus informational gzip line)
```

Clean-room note: written from observed API + standard crypto + vectors. No original source used
or reproduced; decompiled-blob output was never committed (see parent research doc).
