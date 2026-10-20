# Black-box CDT.dll behavior vectors (Windows PowerShell 5.1, Framework CLR).
# Synthetic inputs only. NEVER calls GetADA*/GetADAcodes (ADA-copyrighted content).
# Goal: pin API semantics (round-trips, determinism, formats, gate behavior) for interop analysis.
param(
  [string]$DllPath = (Join-Path (Split-Path (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent) -Parent) 'Required dlls\CDT.dll'),
  [string]$OutPath = (Join-Path $PSScriptRoot 'vectors_cdt.json')
)
Add-Type -Path $DllPath
$v = [ordered]@{}
$v['generator'] = 'original CDT.dll'
$v['asmVersion'] = ([Reflection.AssemblyName]::GetAssemblyName($DllPath)).Version.ToString()
$v['clr'] = [Environment]::Version.ToString()

# 1. The gate, called from a non-OD host (powershell.exe): pass or throw?
try {
  [CDT.Class1]::VerifyCallingAssembly()
  $v['VerifyCallingAssembly_from_powershell'] = @{ threw = $false }
}
catch {
  $v['VerifyCallingAssembly_from_powershell'] = @{ threw = $true; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
}

# 2. TryEncrypt/TryDecrypt round-trips + determinism
foreach ($pt in @('', 'x', 'synthetic-probe-456', ('q' * 100))) {
  try {
    $e1 = [CDT.Class1]::TryEncrypt($pt)
    $e2 = [CDT.Class1]::TryEncrypt($pt)
    $d = [CDT.Class1]::TryDecrypt($e1)
    $v["TryRT_len$($pt.Length)"] = @{ ok = $true; encLen = $e1.Length; deterministic = ($e1 -eq $e2); roundtrip = ($d -eq $pt); encPrefix = $e1.Substring(0, [Math]::Min(16, $e1.Length)) }
  }
  catch {
    $v["TryRT_len$($pt.Length)"] = @{ ok = $false; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
  }
}

# 3. Encrypt/Decrypt (bool,out) + Conceal/Reveal round-trips
foreach ($pair in @(@('Encrypt', 'Decrypt'), @('ConcealClearinghouse', 'RevealClearinghouse'))) {
  $mE, $mD = $pair
  try {
    $c = ''
    $okE = [CDT.Class1]::$mE('synthetic-probe-123', [ref]$c)
    $r = ''
    $okD = [CDT.Class1]::$mD($c, [ref]$r)
    $v["Pair_$mE"] = @{ okE = $okE; okD = $okD; encLen = $c.Length; roundtrip = ($r -eq 'synthetic-probe-123') }
  }
  catch {
    $v["Pair_$mE"] = @{ threw = $true; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
  }
}

# 3b. Sftp pair: EncryptSftp(string)->string; DecryptSftp(string,[ref])->bool
try {
  $sftpEnc = [CDT.Class1]::EncryptSftp('synthetic-probe-123')
  $sftpDec = ''
  $okSftp = [CDT.Class1]::DecryptSftp($sftpEnc, [ref]$sftpDec)
  $v['Pair_EncryptSftp'] = @{ okE = $true; okD = $okSftp; encLen = $sftpEnc.Length; roundtrip = ($sftpDec -eq 'synthetic-probe-123'); encPrefix = $sftpEnc.Substring(0, [Math]::Min(16, $sftpEnc.Length)) }
}
catch {
  $v['Pair_EncryptSftp'] = @{ threw = $true; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
}
foreach ($b in @($true, $false)) {
  try {
    $h = [CDT.Class1]::CreateSaltedHash('synthetic', $b)
    $v["SaltedHash_$b"] = @{ ok = $true; len = $h.Length; value = $h }
  }
  catch {
    $v["SaltedHash_$b"] = @{ ok = $false; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
  }
}

# 5. ValidateKey + GenerateRandKey shape
foreach ($k in @('', 'synthetic', ('0' * 64))) {
  try {
    $v["ValidateKey_len$($k.Length)"] = @{ ok = $true; result = [CDT.Class1]::ValidateKey($k) }
  }
  catch {
    $v["ValidateKey_len$($k.Length)"] = @{ ok = $false; type = $_.Exception.GetType().FullName }
  }
}
try {
  $rk = [CDT.Class1]::GenerateRandKey()
  $v['GenerateRandKey'] = @{ ok = $true; len = $rk.Length; value = $rk }
}
catch {
  $v['GenerateRandKey'] = @{ ok = $false; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
}

# 6. TryDecrypt of garbage (error behavior, not content)
try {
  $v['TryDecrypt_garbage'] = @{ result = [CDT.Class1]::TryDecrypt('!!!not-valid!!!') }
}
catch {
  $v['TryDecrypt_garbage'] = @{ threw = $true; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
}

[IO.File]::WriteAllText($OutPath, ($v | ConvertTo-Json -Depth 6))
Write-Output "wrote $OutPath"
