using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rorrim.Server.Agents;
using Rorrim.Server.Auth;
using Rorrim.Server.Broker;
using Rorrim.Server.Services;
using Rorrim.Server.Sessions;

namespace Rorrim.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        BrokerLog.Write("SERVER MAIN ENTERED");

        if (args.Length > 0 && args[0].Equals("--install-service", StringComparison.OrdinalIgnoreCase))
        {
            ServiceInstaller.Install(string.Join(' ', args.Skip(1)));
            Console.WriteLine("Installed service as LocalSystem. Start it with: sc start \"" + ServiceInstaller.ServiceName + "\"");
            return 0;
        }
        if (args.Length > 0 && args[0].Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase))
        {
            ServiceInstaller.Uninstall();
            Console.WriteLine("Uninstalled service.");
            return 0;
        }

        var builder = WebApplication.CreateBuilder(args);

        int port = GetArg(args, "--port", 50051);
        int agentPort = GetArg(args, "--agent-port", 50052);
        bool testMode = args.Any(a => a.Equals("--test-mode", StringComparison.OrdinalIgnoreCase));
        string certStore = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rorrim", "certs");
        string agentPath = Path.Combine(AppContext.BaseDirectory, "Rorrim.Agent.exe");
        int agentAttachTimeoutMs = 15000;

        foreach (var arg in args)
        {
            var parts = arg.Split('=', 2);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("--cert-store", StringComparison.OrdinalIgnoreCase)) certStore = parts[1];
            else if (parts[0].Equals("--agent", StringComparison.OrdinalIgnoreCase)) agentPath = parts[1];
        }

        BrokerLog.Write("Server starting.");

        var ca = new CertificateAuthority(certStore, Environment.MachineName);

        builder.Services.AddSingleton<ICertificateAuthority>(ca);
        builder.Services.AddSingleton<ISessionProvider, WtsSessionProvider>();
        builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();
        builder.Services.AddSingleton<ISessionBroker, SessionBroker>();
        builder.Services.AddSingleton<IAgentProcessLauncher>(_ => new AgentProcessLauncher(agentPath));
        builder.Services.AddSingleton<ISessionCoordinator>(sp => new SessionCoordinator(
            sp.GetRequiredService<ISessionProvider>(),
            sp.GetRequiredService<IAgentProcessLauncher>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ISessionBroker>(),
            $"http://localhost:{agentPort}",
            TimeSpan.FromMilliseconds(agentAttachTimeoutMs),
            BrokerLog.Write));
        builder.Services.AddSingleton<RorrimClientService>();
        builder.Services.AddSingleton<RorrimAgentService>();

        builder.Services.AddGrpc();

        builder.Services.Configure<KestrelServerOptions>(options =>
        {
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = ca.CaCertificate;
                    if (testMode)
                    {
                        https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                    }
                    else
                    {
                        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        https.ClientCertificateValidation = (_, _, _) => true;
                    }
                });
            });

            options.ListenLocalhost(agentPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });

            options.ListenLocalhost(port + 2, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });
        });

        builder.Services.AddWindowsService(o => o.ServiceName = "Rorrim Server");

        var app = builder.Build();
        app.MapGrpcService<RorrimClientService>();
        app.MapGrpcService<RorrimAgentService>();

        await app.RunAsync();
        return 0;
    }

    private static int GetArg(IReadOnlyList<string> args, string name, int def)
    {
        foreach (var a in args)
        {
            var parts = a.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1], out int v))
                return v;
        }
        return def;
    }
}
