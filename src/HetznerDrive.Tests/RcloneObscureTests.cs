using HetznerDrive.Core;
using Xunit;

namespace HetznerDrive.Tests;

/// <summary>
/// rclone rejects a plaintext password, so every backend config depends on this encoding being
/// exactly right. A wrong implementation fails at mount time with an opaque base64 error, which is
/// precisely the kind of thing that should be caught here instead.
/// </summary>
public class RcloneObscureTests
{
    [Theory]
    [InlineData("")]
    [InlineData("hunter2")]
    [InlineData("a")]
    [InlineData("exactly-16-chars")]
    [InlineData("a password that is definitely longer than one aes block")]
    [InlineData("pässwörd-with-ünicode-字符")]
    public void ObscureThenReveal_RoundTrips(string plaintext)
    {
        var obscured = RcloneObscure.Obscure(plaintext);
        Assert.Equal(plaintext, RcloneObscure.Reveal(obscured));
    }

    [Fact]
    public void Obscure_UsesBase64UrlWithoutPadding()
    {
        // rclone decodes with Go's RawURLEncoding, which rejects '+', '/' and '=' outright.
        var obscured = RcloneObscure.Obscure("some password value");
        Assert.DoesNotContain('+', obscured);
        Assert.DoesNotContain('/', obscured);
        Assert.DoesNotContain('=', obscured);
    }

    [Fact]
    public void Obscure_IsSaltedByARandomIv()
    {
        // A fresh IV per call means the same password never produces the same ciphertext, which is
        // what stops a config file from leaking that two remotes share a password.
        var a = RcloneObscure.Obscure("same password");
        var b = RcloneObscure.Obscure("same password");
        Assert.NotEqual(a, b);
        Assert.Equal(RcloneObscure.Reveal(a), RcloneObscure.Reveal(b));
    }

    [Fact]
    public void Obscure_PrependsA16ByteIv()
    {
        // 16-byte IV + 7 bytes of ciphertext = 23 bytes -> ceil(23/3)*4 = 32, minus 1 pad char.
        var obscured = RcloneObscure.Obscure("hunter2");
        Assert.Equal(31, obscured.Length);
    }

    [Fact]
    public void Reveal_RejectsAValueTooShortToHoldAnIv()
    {
        Assert.Throws<FormatException>(() => RcloneObscure.Reveal("YWJj"));
    }
}
