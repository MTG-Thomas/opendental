# Generates wire-compat test vectors from the ORIGINAL committed ODCrypt.dll.
# Must run under Windows PowerShell 5.1 (.NET Framework CLR — the blob's supported runtime).
# All inputs are fixed except key/IV sampling (lengths/formats only). No patient data.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/make_vectors.ps1
param(
  [string]$DllPath = (Join-Path (Split-Path (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent) -Parent) 'Required dlls\ODCrypt.dll'),
  [string]$OutPath = (Join-Path $PSScriptRoot 'vectors.json')
)
Add-Type -Path $DllPath
function B64([byte[]]$b) { if ($null -eq $b) { return $null }; [Convert]::ToBase64String($b) }
function Hex([byte[]]$b) { if ($null -eq $b) { return $null }; -join ($b | ForEach-Object { $_.ToString('x2') }) }

$v = [ordered]@{}
$v['generator'] = 'original ODCrypt.dll'
$v['asmVersion'] = ([Reflection.AssemblyName]::GetAssemblyName($DllPath)).Version.ToString()
$v['clr'] = [Environment]::Version.ToString()

# Key / IV sampling (values random per run — record shape, not content)
$k = [ODCrypt.Encryption]::GenerateKey()
$i = [ODCrypt.Encryption]::GenerateIV()
$v['GenerateKey'] = @{ len = $k.Length; sample = $k }
$v['GenerateIV'] = @{ len = $i.Length; sample = $i }
$v['STATIC_INIT_VECTOR'] = [ODCrypt.Encryption]::STATIC_INIT_VECTOR

# Fixed key/IV for deterministic vectors (32-char key, 16-char IV, ASCII)
$key = '0123456789ABCDEF0123456789ABCDEF'
$iv = '0123456789ABCDEF'

$stbAscii = [ODCrypt.Encryption]::StringToBytes('ABC', $true)
$stbUni = [ODCrypt.Encryption]::StringToBytes('ABC', $false)
$v['StringToBytes_ABC_ascii'] = B64 $stbAscii
$v['StringToBytes_ABC_unicode'] = B64 $stbUni
$v['BytesToString_ascii_hex'] = [ODCrypt.Encryption]::BytesToString($stbAscii, $true)
$v['BytesToString_ascii_raw'] = [ODCrypt.Encryption]::BytesToString($stbAscii, $false)

$enc = [ODCrypt.Encryption]::EncodeForUnicode('hello test ~!')
$v['EncodeForUnicode'] = $enc
$v['DecodeForUnicode'] = [ODCrypt.Encryption]::DecodeForUnicode($enc)

foreach ($wf in @($true, $false)) {
  $encrypted = ''; $fail = ''
  $ok = [ODCrypt.Encryption]::EncryptString('Linux PoC synthetic plaintext 123', $wf, $key, $iv, [ref]$encrypted, [ref]$fail)
  $v["EncryptString_web$wf"] = @{ ok = $ok; fail = $fail; encrypted = $encrypted }
  $plain = ''; $fail2 = ''
  $ok2 = [ODCrypt.Encryption]::DecryptString($encrypted, $wf, $key, $iv, [ref]$plain, [ref]$fail2)
  $v["DecryptString_web$wf"] = @{ ok = $ok2; fail = $fail2; plain = $plain }
}
# Default-key overloads
$encryptedD = ''; $failD = ''
$okD = [ODCrypt.Encryption]::EncryptString('default key path', $true, [ref]$encryptedD, [ref]$failD)
$v['EncryptString_defaultkey'] = @{ ok = $okD; fail = $failD; encrypted = $encryptedD }
$plainD = ''; $failD2 = ''
$okD2 = [ODCrypt.Encryption]::DecryptString($encryptedD, $true, [ref]$plainD, [ref]$failD2)
$v['DecryptString_defaultkey'] = @{ ok = $okD2; fail = $failD2; plain = $plainD }

# Wrong-key negative (must fail gracefully, not throw)
$plainW = ''; $failW = ''
$okW = [ODCrypt.Encryption]::DecryptString($v['EncryptString_webTrue'].encrypted, $true, 'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF', $iv, [ref]$plainW, [ref]$failW)
$v['DecryptString_wrongkey'] = @{ ok = $okW; fail = $failW; plain = $plainW }

$zipped = ''; $failZ = ''
$okZ = [ODCrypt.Encryption]::CompressString(('synthetic ' * 50), [ref]$zipped, [ref]$failZ)
$v['CompressString'] = @{ ok = $okZ; fail = $failZ; zipped = $zipped }
$unzipped = ''; $failU = ''
$okU = [ODCrypt.Encryption]::DecompressString($zipped, [ref]$unzipped, [ref]$failU)
$v['DecompressString'] = @{ ok = $okU; fail = $failU; plainLen = $unzipped.Length; plainPrefix = $unzipped.Substring(0, 20) }

$v['MD5_Hash_abc_ascii_b64'] = B64 ([ODCrypt.MD5]::Hash($stbAscii))
$v['MD5_Hash_abc_ascii_hex'] = Hex ([ODCrypt.MD5]::Hash($stbAscii))
$v['Sha3_Hash_abc_ascii_b64'] = B64 ([ODCrypt.Sha3]::Hash($stbUni))
$v['Sha3_Hash_abc_ascii_hex'] = Hex ([ODCrypt.Sha3]::Hash($stbUni))
$salt = [ODCrypt.CryptUtil]::GenerateSalt(16)
$v['GenerateSalt_16'] = @{ len = $salt.Length; value = "$salt" }
$v['ConstantEquals_tt'] = [ODCrypt.CryptUtil]::ConstantEquals([byte[]](1, 2, 3), [byte[]](1, 2, 3))
$v['ConstantEquals_tf'] = [ODCrypt.CryptUtil]::ConstantEquals([byte[]](1, 2, 3), [byte[]](1, 2, 4))
$v['RandomString_10_len'] = ([ODCrypt.CryptUtil]::RandomString(10)).Length
$v['RandomString_10_val'] = [ODCrypt.CryptUtil]::RandomString(10)
$a1 = [byte[]](1, 2, 3)
$v['Arrays_clone_eq'] = [ODCrypt.Arrays]::AreEqual($a1, [ODCrypt.Arrays]::Clone($a1))

# Corrected hex key/IV (64-hex key = 32 bytes, 32-hex IV = 16 bytes)
$key64 = '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF'
$iv32 = '0123456789ABCDEF0123456789ABCDEF'
$v['key64_len'] = $key64.Length
$v['iv32_len'] = $iv32.Length
foreach ($wf in @($true, $false)) {
  foreach ($pt in @('', '1234567890123456', '12345678901234567890123456789012', 'Linux PoC synthetic plaintext 123')) {
    $e2 = ''; $f2 = ''
    $okE = [ODCrypt.Encryption]::EncryptString($pt, $wf, $key64, $iv32, [ref]$e2, [ref]$f2)
    $tag = "web$wf" + "_len$($pt.Length)"
    $v["Encrypt2_$tag"] = @{ ok = $okE; fail = $f2; encrypted = $e2 }
    $p2 = ''; $f3 = ''
    $okD3 = [ODCrypt.Encryption]::DecryptString($e2, $wf, $key64, $iv32, [ref]$p2, [ref]$f3)
    $v["Decrypt2_$tag"] = @{ ok = $okD3; fail = $f3; plain = $p2 }
  }
}
$sU = 'h' + [char]0xE9 + 'llo w' + [char]0xF6 + 'rld ' + [char]0x2713 + ' ' + [char]0x2192
$sAcc = 'h' + [char]0xE9 + 'llo'
$eAcc = ''; $fAcc = ''
$okAcc = [ODCrypt.Encryption]::EncryptString($sAcc, $true, $key64, $iv32, [ref]$eAcc, [ref]$fAcc)
$v['Encrypt_accent'] = @{ ok = $okAcc; fail = $fAcc; encrypted = $eAcc }
$pAcc = ''; $fAcc2 = ''
$okAcc2 = [ODCrypt.Encryption]::DecryptString($eAcc, $true, $key64, $iv32, [ref]$pAcc, [ref]$fAcc2)
$v['Decrypt_accent'] = @{ ok = $okAcc2; fail = $fAcc2; plain = $pAcc }
$encU = [ODCrypt.Encryption]::EncodeForUnicode($sU)
$v['EncodeForUnicode_nonascii'] = $encU
$v['DecodeForUnicode_nonascii'] = [ODCrypt.Encryption]::DecodeForUnicode($encU)
# Encoding edge probes: Latin-1 vs windows-1252, escape boundary, lone-NUL decode
$edgeBytes = [byte[]](0x80, 0x90, 0xFF)
$v['BytesToString_edge_raw'] = [ODCrypt.Encryption]::BytesToString($edgeBytes, $false)
$v['BytesToString_edge_hex'] = [ODCrypt.Encryption]::BytesToString($edgeBytes, $true)
$rtEdge = [ODCrypt.Encryption]::StringToBytes([ODCrypt.Encryption]::BytesToString($edgeBytes, $false), $false)
$v['StringToBytes_edge_roundtrip'] = @{ inLen = $edgeBytes.Length; outB64 = B64 $rtEdge }
$sDel = 'a' + [char]0x7F + 'b'
$v['EncodeForUnicode_DEL'] = [ODCrypt.Encryption]::EncodeForUnicode($sDel)
try {
  $v['DecodeForUnicode_loneNUL'] = [ODCrypt.Encryption]::DecodeForUnicode('a' + [char]0x00 + 'ZZZZ')
} catch {
  $v['DecodeForUnicode_loneNUL'] = @{ threw = $_.Exception.GetType().FullName; message = $_.Exception.Message }
}
$v['DecodeForUnicode_validNUL'] = [ODCrypt.Encryption]::DecodeForUnicode('A' + [char]0x00 + '00e9')

# --- seeded fuzz differential (regenerable: System.Random(42)) ---
$r = New-Object Random(42)
function RandHex($n) { -join ((1..$n) | ForEach-Object { '0123456789ABCDEF'[$r.Next(16)] }) }
function RandLatin($n) { -join ((1..$n) | ForEach-Object { [char]$r.Next(256) }) }
$fuzz = @()
for ($i = 0; $i -lt 100; $i++) {
  $pt = RandLatin $r.Next(81)
  $fk = RandHex 64; $fiv = RandHex 32; $fwf = ($r.Next(2) -eq 0)
  $fe = ''; $ff = ''
  $fok = [ODCrypt.Encryption]::EncryptString($pt, $fwf, $fk, $fiv, [ref]$fe, [ref]$ff)
  $fuzz += @{ pt = $pt; wf = $fwf; key = $fk; iv = $fiv; ok = $fok; enc = $fe }
}
$v['Fuzz_encrypt'] = $fuzz
$fuzzH = @()
for ($i = 0; $i -lt 20; $i++) {
  $n = $r.Next(129)
  $bytes = [byte[]]::new($n)
  for ($j = 0; $j -lt $n; $j++) { $bytes[$j] = $r.Next(256) }
  $b64 = [Convert]::ToBase64String($bytes)
  $fuzzH += @{ input = $b64; md5 = -join (([ODCrypt.MD5]::Hash($bytes)) | ForEach-Object { $_.ToString('x2') }); sha3 = -join (([ODCrypt.Sha3]::Hash($bytes)) | ForEach-Object { $_.ToString('x2') }) }
}
$v['Fuzz_hash'] = $fuzzH
$fuzzU = @()
for ($i = 0; $i -lt 20; $i++) {
  $s = -join ((1..$r.Next(40)) | ForEach-Object { [char]$r.Next(512) })
  $enc = [ODCrypt.Encryption]::EncodeForUnicode($s)
  try {
    $dec = [ODCrypt.Encryption]::DecodeForUnicode($enc)
    $fuzzU += @{ plain = $s; encoded = $enc; decodeOk = $true; decoded = $dec }
  } catch {
    $fuzzU += @{ plain = $s; encoded = $enc; decodeOk = $false; decodeThrow = $_.Exception.Message }
  }
}
$v['Fuzz_unicode'] = $fuzzU

[IO.File]::WriteAllText($OutPath, ($v | ConvertTo-Json -Depth 6))
Write-Output "wrote $OutPath"
