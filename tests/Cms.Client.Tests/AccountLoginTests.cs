using System.IO.Pipes;
using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>The club account on the PC: the login settings of a platform, the plan of the client start, the pipe client check.</summary>
public sealed class AccountLoginTests
{
    private static readonly PlatformLogin Steam = new() { ClientPath = @"C:\Program Files (x86)\Steam\steam.exe", LoginArguments = "-login {login} {secret}" };

    private static string Expand(string s) => s.Replace("%ProgramFiles%", @"C:\Program Files");

    [Fact]
    public void Only_an_absolute_exe_with_the_login_token_is_usable()
    {
        Assert.True(AccountLogin.IsUsable(Steam));
        Assert.True(AccountLogin.IsUsable(new PlatformLogin { ClientPath = @"C:\Games\Client.EXE", LoginArguments = "--user={login}" }));
        Assert.True(AccountLogin.IsUsable(new PlatformLogin { ClientPath = @"%ProgramFiles%\Epic Games\Launcher.exe", LoginArguments = "{login}" }));
        Assert.False(AccountLogin.IsUsable(null));
        Assert.False(AccountLogin.IsUsable(new PlatformLogin { ClientPath = @"steam.exe", LoginArguments = "-login {login} {secret}" }));        // relative
        Assert.False(AccountLogin.IsUsable(new PlatformLogin { ClientPath = @"C:\Steam\steam.bat", LoginArguments = "-login {login} {secret}" })); // not an exe
        Assert.False(AccountLogin.IsUsable(new PlatformLogin { ClientPath = @"C:\Steam\..\cmd.exe", LoginArguments = "{login}" }));               // ..
        Assert.False(AccountLogin.IsUsable(new PlatformLogin { ClientPath = @"C:\Steam\steam.exe", LoginArguments = "-login {secret}" }));       // no login
        Assert.False(AccountLogin.IsUsable(new PlatformLogin { ClientPath = @"C:\Steam\steam.exe", LoginArguments = "-login {login}\n{secret}" }));
    }

    [Fact]
    public void The_plan_puts_login_and_secret_into_single_arguments()
    {
        var plan = AccountLogin.Plan(Steam, "club user", "p a\"ss word", Expand)!;
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", plan.FileName);
        // Spaces and quotes stay inside one argument: the process gets an argument list, not a command line to parse
        Assert.Equal(new[] { "-login", "club user", "p a\"ss word" }, plan.Arguments);
        var joined = AccountLogin.Plan(new PlatformLogin { ClientPath = @"%ProgramFiles%\L\launcher.exe", LoginArguments = "--user={login} --pass={secret}" }, "u", "s", Expand)!;
        Assert.Equal(@"C:\Program Files\L\launcher.exe", joined.FileName);
        Assert.Equal(new[] { "--user=u", "--pass=s" }, joined.Arguments);
        Assert.Null(AccountLogin.Plan(Steam, null, "s", Expand));
        Assert.Null(AccountLogin.Plan(Steam, "u", null, Expand));
        Assert.Null(AccountLogin.Plan(new PlatformLogin { ClientPath = "steam.exe", LoginArguments = "{login}" }, "u", "s", Expand));
    }

    [Fact]
    public void The_settings_file_round_trips_and_finds_a_platform_in_any_case()
    {
        var settings = new AccountPoolSettings { Platforms = new Dictionary<string, PlatformLogin> { ["steam"] = Steam } };
        var parsed = AccountPoolSettings.Parse(settings.ToJson())!;
        Assert.Equal(Steam.LoginArguments, parsed.For("STEAM")!.LoginArguments);
        Assert.Null(parsed.For("epic"));
        Assert.Null(parsed.For(null));
        Assert.Null(AccountPoolSettings.Parse("not json"));
        Assert.Empty(AccountPoolSettings.None.Platforms);
    }

    [Fact]
    public void Only_the_installed_launcher_is_a_trusted_pipe_client()
    {
        const string launcher = @"C:\ClubLauncher\Cms.Launcher.exe";
        Assert.True(AgentPipeClient.IsTrustedLauncher(@"C:\ClubLauncher\Cms.Launcher.exe", launcher));
        Assert.True(AgentPipeClient.IsTrustedLauncher(@"c:\clublauncher\CMS.LAUNCHER.EXE", launcher));
        Assert.True(AgentPipeClient.IsTrustedLauncher("C:/ClubLauncher/Cms.Launcher.exe", launcher));
        Assert.False(AgentPipeClient.IsTrustedLauncher(@"C:\Users\player\Desktop\Cms.Launcher.exe", launcher));
        Assert.False(AgentPipeClient.IsTrustedLauncher(@"C:\ClubLauncher\Cms.Launcher.exe.evil.exe", launcher));
        Assert.False(AgentPipeClient.IsTrustedLauncher(null, launcher));
        Assert.False(AgentPipeClient.IsTrustedLauncher(launcher, null));
    }

    /// <summary>
    /// Windows test 2026-11-27, defect 3: the agent (LocalService) now reads the program with QueryFullProcessImageName while
    /// it impersonates the client. The path is compared in one normalized form; anything doubtful is refused.
    /// </summary>
    [Theory]
    [InlineData(@"C:\ClubLauncher\Cms.Launcher.exe", true)]
    [InlineData(@"c:\CLUBLAUNCHER\cms.launcher.EXE", true)]
    [InlineData(@"\\?\C:\ClubLauncher\Cms.Launcher.exe", true)]          // long-path form
    [InlineData(@"\??\C:\ClubLauncher\Cms.Launcher.exe", true)]          // NT form
    [InlineData(@"C:\ClubLauncher\\.\Cms.Launcher.exe", true)]
    [InlineData(@"C:\Temp\..\ClubLauncher\Cms.Launcher.exe", true)]
    [InlineData(@" C:/ClubLauncher/Cms.Launcher.exe ", true)]
    [InlineData(@"C:\ClubLauncher\Cms.Launcher.exe\..\..\Users\ClubUser\Cms.Launcher.exe", false)]
    [InlineData(@"D:\ClubLauncher\Cms.Launcher.exe", false)]
    [InlineData(@"C:\ClubLauncher\Cms.Launcher.exe:evil", false)]          // alternate data stream
    [InlineData(@"C:\ClubLauncher\Cms.Launcher", false)]
    [InlineData(@"\\server\share\ClubLauncher\Cms.Launcher.exe", false)] // network path
    [InlineData(@"\\?\UNC\server\share\Cms.Launcher.exe", false)]
    [InlineData(@"ClubLauncher\Cms.Launcher.exe", false)]                 // relative
    [InlineData(@"C:\..\ClubLauncher\Cms.Launcher.exe", false)]           // above the drive
    [InlineData(@"C:\ClubLauncher\Cms.Launcher.exe" + "\n", true)]       // trimmed
    [InlineData("C:\\ClubLauncher\\Cms\u0001Launcher.exe", false)]
    [InlineData("", false)]
    public void The_program_path_of_the_pipe_client_is_compared_in_a_normalized_form(string client, bool trusted)
    {
        Assert.Equal(trusted, LauncherPath.Same(client, @"C:\ClubLauncher\Cms.Launcher.exe"));
        Assert.Equal(trusted, AgentPipeClient.IsTrustedLauncher(client, "c:/clublauncher/./Cms.Launcher.exe"));
    }

    [Theory]
    [InlineData(2, "GameNotFound")]
    [InlineData(3, "GameNotFound")]
    [InlineData(267, "GameNotFound")]
    [InlineData(5, "GameNoAccess")]
    [InlineData(740, "GameNeedsAdmin")]
    [InlineData(1155, "GameStartError")]
    [InlineData(null, "GameStartError")]
    public void A_program_that_did_not_start_gets_a_reason_in_the_club_language(int? win32Error, string text)
    {
        foreach (var t in new[] { LauncherTexts.Uk, LauncherTexts.En })
            Assert.Equal(t.All()[text], AccountMessages.StartError(win32Error, t));
    }

    [Fact]
    public void A_club_account_goes_only_to_the_installed_lock_screen_in_the_console_session()
    {
        const string launcher = @"C:\ClubLauncher\Cms.Launcher.exe";
        Assert.Equal(PipeClientVerdict.Trusted, PipeClientCheck.Decide(launcher, launcher, 1, 1, consoleOnly: true));
        Assert.Equal(PipeClientVerdict.Trusted, PipeClientCheck.Decide(@"\\?\c:\clublauncher\cms.launcher.exe", launcher, 2, 2, true));
        // The program could not be read (it ended, or the impersonation failed): fail closed
        Assert.Equal(PipeClientVerdict.NoProgram, PipeClientCheck.Decide(null, launcher, 1, 1, true));
        Assert.Equal(PipeClientVerdict.NoProgram, PipeClientCheck.Decide("", launcher, 1, 1, true));
        Assert.Equal(PipeClientVerdict.OtherProgram, PipeClientCheck.Decide(@"C:\Users\ClubUser\Desktop\Cms.Launcher.exe", launcher, 1, 1, true));
        Assert.Equal(PipeClientVerdict.OtherProgram, PipeClientCheck.Decide(launcher, "", 1, 1, true));
        // Session 0 (services) or an unknown session
        Assert.Equal(PipeClientVerdict.ServiceSession, PipeClientCheck.Decide(launcher, launcher, 0, 0, true));
        Assert.Equal(PipeClientVerdict.ServiceSession, PipeClientCheck.Decide(launcher, launcher, null, 1, false));
        // Another session than the console (for example a remote desktop session), or no console session (0xFFFFFFFF)
        Assert.Equal(PipeClientVerdict.NotConsoleSession, PipeClientCheck.Decide(launcher, launcher, 3, 1, true));
        Assert.Equal(PipeClientVerdict.NotConsoleSession, PipeClientCheck.Decide(launcher, launcher, 1, 0xFFFFFFFF, true));
        Assert.Equal(PipeClientVerdict.Trusted, PipeClientCheck.Decide(launcher, launcher, 3, 1, consoleOnly: false));
    }

    [Fact]
    public void Refused_account_requests_have_texts_in_both_languages()
    {
        var busy = new PipeResponse(409, "{\"code\":\"no_free_account\",\"args\":[\"steam\",\"6\"]}");
        Assert.Equal("All club accounts of Steam are in use now. Ask the staff.", AccountMessages.For(busy, "steam", LauncherTexts.En));
        Assert.StartsWith("Усі клубні акаунти Steam", AccountMessages.For(busy, "steam", LauncherTexts.Uk));
        Assert.Equal(LauncherTexts.En.SessionGone, AccountMessages.For(new PipeResponse(409, "{\"code\":\"account_needs_session\"}"), "steam", LauncherTexts.En));
        // Windows test 2026-11-27, defect 9: no raw code on the screen of the player
        Assert.Equal("A club account goes only to the installed lock screen of this PC. Ask the staff.",
            AccountMessages.For(new PipeResponse(403, "{\"code\":\"pipe_client_untrusted\"}"), "steam", LauncherTexts.En));
        foreach (var code in new[] { "credential_unreadable", "credential_delivered", "lease_not_found", "bad_answer", "something_new" })
            foreach (var t in new[] { LauncherTexts.Uk, LauncherTexts.En })
            {
                var text = AccountMessages.For(new PipeResponse(409, $"{{\"code\":\"{code}\"}}"), "steam", t);
                Assert.Equal(t.AccountFailed, text);
                Assert.DoesNotContain(code, text);
            }
        Assert.Equal(LauncherTexts.Uk.NoConnection, AccountMessages.For(new PipeResponse(503, "{\"code\":\"server_unreachable\"}"), "steam", LauncherTexts.Uk));
        Assert.Equal("Battle.net", AccountMessages.PlatformName("battlenet"));
        Assert.All(AccountPlatforms.All, p => Assert.False(string.IsNullOrEmpty(AccountMessages.PlatformName(p))));
    }

    [Fact]
    public async Task The_agent_answers_an_account_request_only_when_the_client_check_passes()
    {
        var name = "ccms-test-" + Guid.NewGuid().ToString("N")[..12];
        var handled = new List<string>();
        var stop = new CancellationTokenSource();
        var server = new AgentPipeServer(
            _ => new NamedPipeServerStream(name, PipeDirection.InOut, AgentPipe.MaxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
            (req, _) => { lock (handled) handled.Add(req.Op); return Task.FromResult(new PipeResponse(200, "{\"leaseId\":1,\"platform\":\"steam\",\"loginMask\":\"de•••01\",\"delivered\":true}")); },
            authorize: (_, req) => req.Op != PipeOps.Account);
        var run = Task.Run(() => server.RunAsync(stop.Token));

        var refused = await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Account, GameId: Guid.NewGuid()), TimeSpan.FromSeconds(5));
        Assert.Equal((403, "pipe_client_untrusted"), (refused.Status, refused.Code));
        Assert.True((await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Offers), TimeSpan.FromSeconds(5))).Ok);
        Assert.Equal(new[] { PipeOps.Offers }, handled);

        // A throwing check fails closed
        var stop2 = new CancellationTokenSource();
        var name2 = name + "b";
        var strict = new AgentPipeServer(
            _ => new NamedPipeServerStream(name2, PipeDirection.InOut, AgentPipe.MaxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
            (_, _) => Task.FromResult(new PipeResponse(200, "{}")), authorize: (_, _) => throw new InvalidOperationException("no process"));
        var run2 = Task.Run(() => strict.RunAsync(stop2.Token));
        Assert.Equal(403, (await AgentPipeClient.SendAsync(name2, new PipeRequest(PipeOps.Account, GameId: Guid.NewGuid()), TimeSpan.FromSeconds(5))).Status);

        // The answer of a lease reaches the lock screen as AccountLease (without credentials when handed out already)
        var lease = new PipeResponse(200, "{\"leaseId\":7,\"platform\":\"steam\",\"loginMask\":\"de•••01\",\"delivered\":false,\"login\":\"u\",\"secret\":\"s\"}").As<AccountLease>()!;
        Assert.Equal((7L, "de•••01", "u", "s"), (lease.LeaseId, lease.LoginMask, lease.Login, lease.Secret));
        Assert.Contains(PipeOps.Account, PipeOps.All);
        stop.Cancel();
        stop2.Cancel();
        await run;
        await run2;
    }
}
