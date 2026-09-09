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
            ServiceInstaller.Install(args.Skip(1).ToArray());
            Console.WriteLine($"Installed and started the \"{ServiceInstaller.ServiceName}\" service (LocalSystem, auto-start, restart-on-crash).");
            return 0;
        }
        if (args.Length > 0 && args[0].Equals("--issue-client", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || args[1].StartsWith("--"))
            {
                Console.WriteLine("Usage: Rorrim.Server --issue-client <clientId> [--cert-store <dir>]");
                return 1;
            }
            string store = CommandLineArgs.GetString(args, "--cert-store") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rorrim", "certs");
            var issuerCa = new CertificateAuthority(store, Environment.MachineName);
            var issued = issuerCa.IssueClientCertificate(args[1]);
            Console.WriteLine($"Client certificate issued for '{issued.Subject}'.");
            Console.WriteLine($"  PFX (private key + certificate): {issuerCa.ClientPfxPath(args[1])}");
            Console.WriteLine($"  PFX password: {CertificateAuthority.PfxPassword}");
            Console.WriteLine($"  CA certificate (to pin on the client): {issuerCa.CaPemPath}");
            Console.WriteLine("Copy the PFX to the client machine, then launch:");
            Console.WriteLine($"  Rorrim.Client https://<host>:{CommandLineArgs.GetInt(args, "--port", 50051)} --pfx <path-to-pfx> --pfx-password {CertificateAuthority.PfxPassword} --ca \"{issuerCa.CaPemPath}\"");
            return 0;
        }
        if (args.Length > 0 && args[0].Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase))
        {
            ServiceInstaller.Uninstall();
            Console.WriteLine("Uninstalled service.");
            return 0;
        }

        BrokerLog.Configure(CommandLineArgs.GetString(args, "--log"));
        BrokerLog.Write("Server starting.");

        var builder = WebApplication.CreateBuilder(args);

        int port = CommandLineArgs.GetInt(args, "--port", 50051);
        int agentPort = CommandLineArgs.GetInt(args, "--agent-port", 50052);
        bool testMode = CommandLineArgs.Has(args, "--test-mode");
        string certStore = CommandLineArgs.GetString(args, "--cert-store") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rorrim", "certs");
        string agentPath = CommandLineArgs.GetString(args, "--agent")
            ?? Path.Combine(AppContext.BaseDirectory, "Rorrim.Agent.exe");
        int agentAttachTimeoutMs = 15000;

        var ca = new CertificateAuthority(certStore, Environment.MachineName);

        builder.Services.AddSingleton<ICertificateAuthority>(ca);
        builder.Services.AddSingleton<ISessionProvider, WtsSessionProvider>();
        builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();
        builder.Services.AddSingleton<IAgentTokenStore, InMemoryAgentTokenStore>();
        builder.Services.AddSingleton<ISessionBroker, SessionBroker>();
        builder.Services.AddSingleton<IAgentProcessLauncher>(_ => new AgentProcessLauncher(agentPath));
        builder.Services.AddSingleton<ISessionCoordinator>(sp => new SessionCoordinator(
            sp.GetRequiredService<ISessionProvider>(),
            sp.GetRequiredService<IAgentProcessLauncher>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ISessionBroker>(),
            sp.GetRequiredService<IAgentTokenStore>(),
            $"http://localhost:{agentPort}",
            TimeSpan.FromMilliseconds(agentAttachTimeoutMs),
            BrokerLog.Write));
        builder.Services.AddSingleton<RorrimClientService>();
        builder.Services.AddSingleton<RorrimAgentService>(sp =>
            new RorrimAgentService(
                sp.GetRequiredService<IAgentRegistry>(),
                sp.GetRequiredService<IAgentTokenStore>(),
                allowUnauthenticatedAgents: testMode,
                log: BrokerLog.Write));

        builder.Services.AddGrpc();

        builder.Services.Configure<KestrelServerOptions>(options =>
        {
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = ca.ServerCertificate;
                    if (testMode)
                    {
                        https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                    }
                    else
                    {
                        // Real mTLS: require a client certificate that chains to our CA.
                        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        https.ClientCertificateValidation = (cert, _, _) => ca.IsValidClientCertificate(cert);
                    }
                });
            });

            options.ListenLocalhost(agentPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });

            // Cleartext development endpoint; only exposed in test mode.
            if (testMode)
            {
                options.ListenLocalhost(port + 2, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                });
            }
        });

        builder.Services.AddWindowsService(o => o.ServiceName = "Rorrim Server");

        var app = builder.Build();
        app.MapGrpcService<RorrimClientService>();
        app.MapGrpcService<RorrimAgentService>();

        await app.RunAsync();
        return 0;
    }
}
