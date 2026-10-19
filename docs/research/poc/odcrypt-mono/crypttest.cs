using System;
public class CryptTest {
    public static int Main() {
        string fail = "";
        string encrypted;
        string plain = "Linux PoC synthetic plaintext 123";
        string key = ODCrypt.Encryption.GenerateKey();
        string iv = ODCrypt.Encryption.GenerateIV();
        Console.WriteLine("keyLen=" + key.Length + " ivLen=" + iv.Length);
        if (!ODCrypt.Encryption.EncryptString(plain, true, key, iv, out encrypted, ref fail)) {
            Console.WriteLine("ENCRYPT_FAIL: " + fail); return 1;
        }
        Console.WriteLine("encryptedLen=" + encrypted.Length);
        string decrypted;
        if (!ODCrypt.Encryption.DecryptString(encrypted, true, key, iv, out decrypted, ref fail)) {
            Console.WriteLine("DECRYPT_FAIL: " + fail); return 1;
        }
        Console.WriteLine("decrypted=" + decrypted);
        if (decrypted != plain) { Console.WriteLine("ROUNDTRIP_MISMATCH"); return 1; }
        Console.WriteLine("ODCRYPT_ROUNDTRIP_SUCCESS on " + Environment.OSVersion);
        return 0;
    }
}
