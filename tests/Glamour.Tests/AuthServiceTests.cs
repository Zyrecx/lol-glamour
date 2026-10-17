using Glamour.Host.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-auth-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
    private string FilePath => Path.Combine(_dir, "auth.json");

    private AuthService Create() => new(_time, FilePath, NullLogger<AuthService>.Instance);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private Issued Pair(AuthService a, string? existing = null, string? name = null) =>
        a.Redeem(a.CreatePairing().Secret, existing, name, null)!;

    [Fact]
    public void Pairing_secret_works_once()
    {
        var a = Create();
        var code = a.CreatePairing();
        Assert.NotNull(a.Redeem(code.Secret, null, null, null));
        Assert.Null(a.Redeem(code.Secret, null, null, null));
    }

    [Fact]
    public void Pairing_secret_expires()
    {
        var a = Create();
        var code = a.CreatePairing();
        _time.Advance(AuthService.PairingLifetime + TimeSpan.FromSeconds(1));
        Assert.Null(a.Redeem(code.Secret, null, null, null));
    }

    [Fact]
    public void Unknown_secret_is_refused()
    {
        Assert.Null(Create().Redeem("nope", null, null, null));
    }

    [Fact]
    public void Newest_pending_pairings_win_when_too_many_are_open()
    {
        var a = Create();
        var first = a.CreatePairing();
        for (var i = 0; i < AuthService.MaxPendingPairings; i++) { _time.Advance(TimeSpan.FromSeconds(1)); a.CreatePairing(); }
        Assert.Null(a.Redeem(first.Secret, null, null, null));
    }

    [Fact]
    public void Several_devices_each_get_their_own_token()
    {
        var a = Create();
        var phone = Pair(a, name: "Phone");
        var laptop = Pair(a, name: "Laptop");
        Assert.NotEqual(phone.Token, laptop.Token);
        Assert.NotNull(a.Authenticate(phone.Token));
        Assert.NotNull(a.Authenticate(laptop.Token));
        Assert.Equal(2, a.Devices().Count);
    }

    [Fact]
    public void Revoking_one_device_leaves_the_others()
    {
        var a = Create();
        var phone = Pair(a);
        var laptop = Pair(a);
        Assert.True(a.Revoke(phone.Device.Id));
        Assert.Null(a.Authenticate(phone.Token));
        Assert.NotNull(a.Authenticate(laptop.Token));
        Assert.False(a.Revoke(phone.Device.Id));
    }

    [Fact]
    public void Revoking_cancels_the_devices_streams()
    {
        var a = Create();
        var phone = Pair(a);
        var token = a.RevocationToken(phone.Device.Id);
        Assert.False(token.IsCancellationRequested);
        a.Revoke(phone.Device.Id);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Revoke_all_signs_everyone_out()
    {
        var a = Create();
        var tokens = new[] { Pair(a).Token, Pair(a).Token, Pair(a).Token };
        a.RevokeAll();
        Assert.All(tokens, t => Assert.Null(a.Authenticate(t)));
        Assert.Empty(a.Devices());
    }

    [Fact]
    public void Pairing_again_from_a_signed_in_browser_keeps_one_device_and_rotates_the_token()
    {
        var a = Create();
        var first = Pair(a);
        var second = Pair(a, existing: first.Token);
        Assert.Equal(first.Device.Id, second.Device.Id);
        Assert.Single(a.Devices());
        Assert.Null(a.Authenticate(first.Token));
        Assert.NotNull(a.Authenticate(second.Token));
    }

    [Fact]
    public void A_stale_cookie_does_not_hijack_a_device_slot()
    {
        var a = Create();
        var first = Pair(a);
        var other = Pair(a, existing: "not-a-real-token");
        Assert.NotEqual(first.Device.Id, other.Device.Id);
        Assert.Equal(2, a.Devices().Count);
    }

    [Fact]
    public void Too_many_devices_drops_the_least_recently_used()
    {
        var a = Create();
        var oldest = Pair(a);
        for (var i = 1; i < AuthService.MaxDevices; i++) { _time.Advance(TimeSpan.FromMinutes(1)); Pair(a); }
        _time.Advance(TimeSpan.FromMinutes(1));
        var newest = Pair(a);
        Assert.Equal(AuthService.MaxDevices, a.Devices().Count);
        Assert.Null(a.Authenticate(oldest.Token));
        Assert.NotNull(a.Authenticate(newest.Token));
    }

    [Fact]
    public void Session_expires_after_30_idle_days_but_use_renews_it()
    {
        var a = Create();
        var idle = Pair(a);
        var used = Pair(a);
        _time.Advance(TimeSpan.FromDays(20));
        Assert.NotNull(a.Authenticate(used.Token));
        _time.Advance(TimeSpan.FromDays(20));
        Assert.NotNull(a.Authenticate(used.Token)); // 20 days since last use
        Assert.Null(a.Authenticate(idle.Token));    // 40 days since last use
    }

    [Fact]
    public void Cookie_is_refreshed_at_most_once_a_day()
    {
        var a = Create();
        var d = Pair(a);
        Assert.False(a.Authenticate(d.Token)!.RefreshCookie);
        _time.Advance(TimeSpan.FromDays(2));
        Assert.True(a.Authenticate(d.Token)!.RefreshCookie);
        Assert.False(a.Authenticate(d.Token)!.RefreshCookie);
    }

    [Fact]
    public void Devices_survive_a_restart_and_the_file_holds_no_token()
    {
        var a = Create();
        var phone = Pair(a, name: "Phone");
        a.SetPassword("4821");

        Assert.DoesNotContain(phone.Token, File.ReadAllText(FilePath));
        Assert.DoesNotContain("4821", File.ReadAllText(FilePath));

        var restarted = Create();
        Assert.Equal("Phone", restarted.Authenticate(phone.Token)!.Device.Name);
        Assert.True(restarted.HasPassword);
    }

    [Fact]
    public void Revocation_survives_a_restart()
    {
        var a = Create();
        var phone = Pair(a);
        a.Revoke(phone.Device.Id);
        Assert.Null(Create().Authenticate(phone.Token));
    }

    [Fact]
    public void Corrupt_file_means_nobody_is_signed_in()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var a = Create();
        Assert.Empty(a.Devices());
        Assert.False(a.HasPassword);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("123")]
    [InlineData("abcd")]
    [InlineData("12 34")]
    public void Short_or_non_digit_passwords_are_refused(string pw)
    {
        var a = Create();
        Assert.False(a.SetPassword(pw));
        Assert.False(a.HasPassword);
    }

    [Fact]
    public void Password_login_works_for_any_number_of_browsers()
    {
        var a = Create();
        Assert.True(a.SetPassword("4821"));
        var one = a.Login("4821", null, "One", null);
        var two = a.Login("4821", null, "Two", null);
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one.Device.Id, two.Device.Id);
        Assert.Null(a.Login("wrong password", null, null, null));
    }

    [Fact]
    public void Login_without_a_password_set_fails()
    {
        Assert.Null(Create().Login("anything at all", null, null, null));
    }

    [Fact]
    public void Changing_the_password_keeps_devices_signed_in()
    {
        var a = Create();
        var phone = Pair(a);
        a.SetPassword("1111");
        a.SetPassword("2222");
        Assert.Null(a.Login("1111", null, null, null));
        Assert.NotNull(a.Login("2222", null, null, null));
        Assert.NotNull(a.Authenticate(phone.Token));
    }

    [Theory]
    [InlineData("  Pixel 9  ", null, "", "Pixel 9")]
    [InlineData(null, "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0) AppleWebKit Safari/604.1", "", "Safari on iPhone")]
    [InlineData(null, "Mozilla/5.0 (Windows NT 10.0) Chrome/130 Safari/537 Edg/130", "", "Edge on Windows")]
    [InlineData(null, null, "Old name", "Old name")]
    [InlineData(null, null, "", "Device")]
    public void Device_names(string? given, string? ua, string previous, string expected) =>
        Assert.Equal(expected, AuthService.DeviceName(given, ua, previous));

    [Fact]
    public void Device_name_is_cut_and_stripped_of_control_characters()
    {
        var name = AuthService.DeviceName("a\nb" + new string('x', 100), null, "");
        Assert.Equal(40, name.Length);
        Assert.DoesNotContain('\n', name);
    }
}

public class LoginThrottleTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void Locks_after_five_failures_and_the_lock_grows()
    {
        var t = new LoginThrottle(_time);
        for (var i = 0; i < 5; i++) { Assert.Null(t.RetryAfter("a")); t.Failure("a"); }
        Assert.Null(t.RetryAfter("a")); // five free failures, the sixth is what locks
        t.Failure("a");
        var first = t.RetryAfter("a");
        Assert.NotNull(first);
        _time.Advance(first.Value + TimeSpan.FromSeconds(1));
        Assert.Null(t.RetryAfter("a"));
        t.Failure("a");
        Assert.True(t.RetryAfter("a") > first);
    }

    [Fact]
    public void Lock_is_capped_at_fifteen_minutes()
    {
        var t = new LoginThrottle(_time);
        for (var i = 0; i < 40; i++) t.Failure("a");
        Assert.True(t.RetryAfter("a") <= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Clients_are_separate_and_success_clears()
    {
        var t = new LoginThrottle(_time);
        for (var i = 0; i < 8; i++) t.Failure("a");
        Assert.NotNull(t.RetryAfter("a"));
        Assert.Null(t.RetryAfter("b"));
        t.Success("a");
        Assert.Null(t.RetryAfter("a"));
    }

    [Fact]
    public void Old_failures_are_forgotten()
    {
        var t = new LoginThrottle(_time);
        for (var i = 0; i < 5; i++) t.Failure("a");
        _time.Advance(TimeSpan.FromHours(2));
        t.Failure("a");
        Assert.Null(t.RetryAfter("a"));
    }
}
