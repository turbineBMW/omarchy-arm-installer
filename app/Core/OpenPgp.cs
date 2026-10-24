using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace OmarchyArmInstaller.Core;

/// Checks detached OpenPGP signatures (what gpg --detach-sign and makepkg
/// --sign write) against one key: the key a recipe pins by fingerprint.
public static class OpenPgp
{
    /// The fingerprints of an armored public key's primary key and subkeys.
    public static IReadOnlyList<string> Fingerprints(string armoredKey)
    {
        var list = new List<string>();
        foreach (PgpPublicKeyRing ring in Rings(armoredKey).GetKeyRings())
            foreach (PgpPublicKey key in ring.GetPublicKeys())
                list.Add(Convert.ToHexString(key.GetFingerprint()));
        return list;
    }

    /// True when <signature> is a valid signature over <data> by <armoredKey>,
    /// whose primary fingerprint must be <fingerprint>. The signature may be
    /// binary or armored.
    public static bool Verify(byte[] data, byte[] signature, string armoredKey, string fingerprint) =>
        Verify(new MemoryStream(data), signature, armoredKey, fingerprint);

    /// The same, for data read from a stream (a package file).
    public static bool Verify(Stream data, byte[] signature, string armoredKey, string fingerprint)
    {
        var rings = Rings(armoredKey);
        var ring = rings.GetKeyRings().Cast<PgpPublicKeyRing>().FirstOrDefault(r =>
            Convert.ToHexString(r.GetPublicKey().GetFingerprint()).Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
        if (ring is null) return false;

        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(signature));
        var factory = new PgpObjectFactory(input);
        PgpSignatureList? signatures = null;
        for (var o = factory.NextPgpObject(); o is not null; o = factory.NextPgpObject())
        {
            if (o is PgpSignatureList list) { signatures = list; break; }
            if (o is PgpCompressedData compressed) factory = new PgpObjectFactory(compressed.GetDataStream());
        }
        if (signatures is null) return false;

        for (var i = 0; i < signatures.Count; i++)
        {
            var sig = signatures[i];
            var key = ring.GetPublicKey(sig.KeyId);
            if (key is null || key.HasRevocation()) continue;
            try
            {
                sig.InitVerify(key);
                data.Position = 0;
                var buffer = new byte[1 << 20];
                int read;
                while ((read = data.Read(buffer, 0, buffer.Length)) > 0) sig.Update(buffer, 0, read);
                if (sig.Verify()) return true;
            }
            catch (PgpException)
            {
            }
        }
        return false;
    }

    static PgpPublicKeyRingBundle Rings(string armoredKey)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(armoredKey)));
        return new PgpPublicKeyRingBundle(input);
    }
}
