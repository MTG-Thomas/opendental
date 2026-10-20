# CDT behavior probe (black-box, synthetic inputs only)

Target: `CDT.dll` 24.3.63.0 (committed blob, no source; ADA-licensed content).
Method: vectors generated from the original under .NET Framework (`tools/make_cdt_vectors.ps1`);
execution-environment probes via net48 harness (`src/cdtprobe`) on native Windows (build host),
Mono 6.12 (metadata-only, fails), and Wine 11/wine-mono (same failure as Mono).

**Hard guardrail: NEVER call `GetADA*`/`GetADAcodes`/code-content methods.** Only the
obfuscation/hash/key-format APIs with synthetic strings are probed. No ADA content extracted.

## Findings (all pinned in vectors_cdt.json)

| API | Behavior from non-OD caller |
|---|---|
| `VerifyCallingAssembly()` direct | Throws (`Unable to find assembly to verify.` — stack-shape dependent, not a clean allowlist denial) |
| `TryEncrypt/TryDecrypt` | **Work ungated.** Deterministic, base64 (`+/`), 16-byte blocks w/ PKCS7 shape (1 B→16 B→24 chars; 19 B→32 B→44 chars). Round-trips pass, incl. empty. Garbage `TryDecrypt` **fails open** (returns input). |
| `Encrypt/Decrypt`, `Conceal/RevealClearinghouse` | Work ungated, same 32-byte block shape. Round-trips pass. |
| `EncryptSftp` (`string->string`) / `DecryptSftp` (`string,[ref]->bool`) | Work ungated, same shape, round-trip passes. (Note the asymmetric signatures.) |
| `CreateSaltedHash` | **GATED**: throws `Unable to find assembly to verify.` from non-OD callers. Hash format unrecoverable black-box → port needs a migration strategy (e.g., re-hash on next login), not a reimplementation. |
| `ValidateKey` | Returns `True` for `''`, `'synthetic'`, 64-zeros — Band-AID permissive/format-only. |
| `GenerateRandKey` | Returns **empty string** from non-OD caller (gate swallow or uninitialized path). |
| Execution environments | Framework 4.x native: runs. Mono 6.12: `InvalidProgramException` on every gated/obfuscated method. Wine 11 (wine-mono): identical failure. **Wine 11 + real Framework 4.8 (winetricks `dotnet48`): runs** — obfuscated IL executes (OS reports Win7 SP1, real CLR), `Conceal/Reveal` and `TryEncrypt/Decrypt` round-trip OK from a foreign caller, while direct `VerifyCallingAssembly` throws the true gate message `Method can only be called from an assembly built by Open Dental.` Renaming the harness to `OpenDental.exe` does **not** satisfy the gate (still throws) — the check is not filename-only, killing any rename-to-spoof shortcut definitively. Lab container `winebox` (local Docker, stopped) retains the Framework prefix for reruns. |

## Usage map for a future port (no CDT reimplementation attempted)

- Session/token obfuscation (`TryEncrypt` family) and clearinghouse concealment are portable-shaped
  (deterministic block cipher + base64) and ungated — reimplementable from these vectors if needed.
- Password-hash verification (`CreateSaltedHash`) is the hard gate: it only runs under OD-named
  callers on Framework. Any Linux service doing auth must either run the original binary, negotiate
  the gate, or migrate hashes.
- `GetADA*` content stays inside the blob (EULA). Nothing here extracts it.

## Reproduce

```powershell
# Vectors (Windows PowerShell 5.1):
powershell -NoProfile -ExecutionPolicy Bypass -File tools/make_cdt_vectors.ps1
# Probe harness under Wine (needs absolute C:/ bind paths on this host):
dotnet build src/cdtprobe.csproj -c Release -o bin
MSYS_NO_PATHCONV=1 docker run --rm -v "C:/abs/path/docs/research/poc/cdt-wine/bin:/probe:ro" scottyhardy/docker-wine:latest wine /probe/cdtprobe.exe
```
