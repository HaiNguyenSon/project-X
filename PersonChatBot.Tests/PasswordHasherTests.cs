using PersonChatBot.Auth;
using PersonChatBot.Configuration;

namespace PersonChatBot.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var hash = PasswordHasher.Hash("the-real-password");
        Assert.False(PasswordHasher.Verify("not-the-password", hash));
    }

    [Fact]
    public void Hashing_same_password_twice_gives_different_hashes()
    {
        // Random salt -> different encoded strings, both still verify.
        var a = PasswordHasher.Hash("same");
        var b = PasswordHasher.Hash("same");
        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("same", a));
        Assert.True(PasswordHasher.Verify("same", b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("PBKDF2-SHA256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("WRONGPREFIX$100000$c2FsdA==$aGFzaA==")]
    public void Verify_rejects_malformed_hashes(string encoded)
        => Assert.False(PasswordHasher.Verify("whatever", encoded));

    [Fact]
    public void AuthOptions_uses_the_hash_when_present()
    {
        var options = new AuthOptions { PasswordHash = PasswordHasher.Hash("secret123") };
        Assert.True(options.Enabled);
        Assert.True(options.VerifyPassword("secret123"));
        Assert.False(options.VerifyPassword("wrong"));
    }

    [Fact]
    public void AuthOptions_falls_back_to_plaintext_when_no_hash()
    {
        var options = new AuthOptions { Password = "plain-secret" };
        Assert.True(options.VerifyPassword("plain-secret"));
        Assert.False(options.VerifyPassword("nope"));
    }

    [Fact]
    public void AuthOptions_hash_takes_precedence_over_plaintext()
    {
        var options = new AuthOptions
        {
            Password = "ignored-plaintext",
            PasswordHash = PasswordHasher.Hash("the-hashed-one"),
        };
        Assert.True(options.VerifyPassword("the-hashed-one"));
        Assert.False(options.VerifyPassword("ignored-plaintext"));
    }
}
