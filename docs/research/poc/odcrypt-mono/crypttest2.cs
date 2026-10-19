using System;
public class CryptTest2 {
    public static int Main() {
        Console.WriteLine("runtime=" + Environment.Version + " os=" + Environment.OSVersion);
        // Pure-managed helpers, no keygen/obfuscation path
        byte[] b = ODCrypt.Encryption.StringToBytes("ABC", true);
        Console.WriteLine("StringToBytes len=" + b.Length);
        string s = ODCrypt.Encryption.BytesToString(b, false);
        Console.WriteLine("BytesToString=" + s);
        string enc = ODCrypt.Encryption.EncodeForUnicode("hello");
        string dec = ODCrypt.Encryption.DecodeForUnicode(enc);
        Console.WriteLine("unicode roundtrip=" + (dec == "hello" ? "OK" : "MISMATCH:" + dec));
        byte[] a1 = new byte[]{1,2,3};
        byte[] a2 = ODCrypt.Arrays.Clone(a1);
        Console.WriteLine("Arrays.Clone eq=" + ODCrypt.Arrays.AreEqual(a1, a2));
        if (s != "ABC" || dec != "hello" || !ODCrypt.Arrays.AreEqual(a1, a2)) { Console.WriteLine("MISMATCH"); return 1; }
        Console.WriteLine("ODCRYPT_SIMPLE_ROUNDTRIP_SUCCESS");
        return 0;
    }
}
