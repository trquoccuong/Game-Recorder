using Odx.Core;
using Odx.Games.Csgo;
using Xunit;

namespace Odx.Tests;

/// <summary>The author's certification triplets (Thin-ICE, ICE, ICE-2).</summary>
public class IceTests
{
    [Theory]
    [InlineData(0, "deadbeef01234567", "de240d83a00a9cc0")]
    [InlineData(1, "deadbeef01234567", "7d6ef1ef30d47a96")]
    [InlineData(2, "00112233445566778899aabbccddeeff", "f94840d86972f21c")]
    public void Matches_the_certification_triplets(int level, string key, string cipher)
    {
        var ice = new IceKey(level, Convert.FromHexString(key));
        byte[] plain = Convert.FromHexString("fedcba9876543210"), out1 = new byte[8], out2 = new byte[8];
        ice.EncryptBlock(plain, out1);
        Assert.Equal(cipher, Convert.ToHexString(out1).ToLowerInvariant());
        ice.DecryptBlock(out1, out2);
        Assert.Equal(plain, out2);
    }
}
