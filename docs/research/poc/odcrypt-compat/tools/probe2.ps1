# Black-box probes against the ORIGINAL ODCrypt.dll (Windows PowerShell 5.1).
# 1) RandomString length mapping. 2) Default-key recovery hypothesis (zeros).
# 3) Non-ASCII plaintext encoding. 4) Bad-length key/IV error messages.
param([string]$DllPath = 'Required dlls/ODCrypt.dll')
Add-Type -Path $DllPath
Write-Output '--- RandomString lengths ---'
foreach ($n in @(1, 2, 3, 4, 5, 8, 10, 16, 32)) {
  $s = [ODCrypt.CryptUtil]::RandomString($n)
  Write-Output "RandomString($n) len=$($s.Length) val=$s"
}
Write-Output '--- default-key ciphertext of test ---'
$e = ''; $f = ''
[ODCrypt.Encryption]::EncryptString('test', $true, [ref]$e, [ref]$f) | Out-Null
Write-Output "defkey_ct=$e"
Write-Output '--- zeros hypothesis: decrypt defkey ct with zero key/IV ---'
$z = '0' * 64; $ziv = '0' * 32
$p = ''; $f2 = ''
$ok = [ODCrypt.Encryption]::DecryptString($e, $true, $z, $ziv, [ref]$p, [ref]$f2)
Write-Output "zeros ok=$ok plain='$p' fail='$f2'"
Write-Output '--- SIV-as-key hypothesis ---'
$siv = [ODCrypt.Encryption]::STATIC_INIT_VECTOR
$p3 = ''; $f3 = ''
$ok3 = [ODCrypt.Encryption]::DecryptString($e, $true, $siv, $siv, [ref]$p3, [ref]$f3)
Write-Output "siv ok=$ok3 plain='$p3' fail='$f3'"
Write-Output '--- cross-check: original decompresses portable gzip ---'
$mineGz = 'H4sIAAAAAAAACiuuzCvJSC3JTFYoHmWNkDAAAK7GKB30AQAA'
$pX = ''; $fX = ''
$okX = [ODCrypt.Encryption]::DecompressString($mineGz, [ref]$pX, [ref]$fX)
Write-Output "orig_decompresses_mine ok=$okX len=$($pX.Length)"
$acc = 'h' + [char]0xE9 + 'llo'
$e4 = ''; $f4 = ''
$ok4 = [ODCrypt.Encryption]::EncryptString($acc, $true, $z, $ziv, [ref]$e4, [ref]$f4)
Write-Output "accent ok=$ok4 ct=$e4 fail=$f4"
Write-Output '--- bad-length key/IV errors ---'
$e5 = ''; $f5 = ''
$ok5 = [ODCrypt.Encryption]::EncryptString('x', $true, 'ABCD', $ziv, [ref]$e5, [ref]$f5)
Write-Output "shortkey ok=$ok5 fail=$f5"
$e6 = ''; $f6 = ''
$ok6 = [ODCrypt.Encryption]::EncryptString('x', $true, $z, 'ABCD', [ref]$e6, [ref]$f6)
Write-Output "shortiv ok=$ok6 fail=$f6"
