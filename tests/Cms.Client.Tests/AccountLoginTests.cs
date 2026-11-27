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

    [Fact]
    public void Refused_account_requests_have_texts_in_both_languages()
    {
        var busy = new PipeResponse(409, "{\"code\":\"no_free_account\",\"args\":[\"steam\",\"6\"]}");
        Assert.Equal("All club accounts of Steam are in use now. Ask the staff.", AccountMessages.For(busy, "steam", LauncherTexts.En));
        Assert.StartsWith("Усі клубні акаунти Steam", AccountMessages.For(busy, "steam", LauncherTexts.Uk));
        Assert.Equal(LauncherTexts.En.SessionGone, AccountMessages.For(new PipeResponse(409, "{\"code\":\"account_needs_session\"}"), "steam", LauncherTexts.En));
        Assert.Equal("The club account could not be given (pipe_client_untrusted). Ask the staff.",
            AccountMessages.For(new PipeResponse(403, "{\"code\":\"pipe_client_untrusted\"}"), "steam", LauncherTexts.En));
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
