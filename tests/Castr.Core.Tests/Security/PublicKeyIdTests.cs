using Castr.Core.Security;

namespace Castr.Core.Tests.Security;

public class PublicKeyIdTests
{
    [Fact]
    public void FromRawEd25519_ThenToRawEd25519_RoundTrips()
    {
        var raw = new byte[32];
        new Random(1).NextBytes(raw);

        var id = PublicKeyId.FromRawEd25519(raw);

        Assert.Equal(raw, id.ToRawEd25519());
    }

    [Fact]
    public void FromRawEd25519_HasEd25519Prefix()
    {
        var raw = new byte[32];
        Assert.StartsWith("ed25519:", PublicKeyId.FromRawEd25519(raw).ToString());
    }

    [Fact]
    public void FromRawEd25519_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => PublicKeyId.FromRawEd25519(new byte[16]));
    }

    [Fact]
    public void ToRawEd25519_MissingPrefix_Throws()
    {
        var id = new PublicKeyId("not-a-valid-id");
        Assert.Throws<FormatException>(() => id.ToRawEd25519());
    }

    [Fact]
    public void Equality_SameKeyBytes_AreEqual()
    {
        var raw = new byte[32];
        new Random(2).NextBytes(raw);

        Assert.Equal(PublicKeyId.FromRawEd25519(raw), PublicKeyId.FromRawEd25519(raw));
    }
}
