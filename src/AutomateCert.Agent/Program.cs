using AutomateCert.Agent;
using AutomateCert.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AutomateCertAgent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<PendingKeyStore>();
builder.Services.AddSingleton<CertificateInstaller>();
builder.Services.AddSingleton<AdcsClient>();
builder.Services.AddSingleton<PowerShellRunner>();
builder.Services.AddSingleton<TlsVerifier>();
builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<DeploymentEngine>();
builder.Services.AddSingleton<ServerClient>();
builder.Services.AddHttpClient(ServerClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(sp => ServerClient.CreateHandler(sp));
builder.Services.AddHostedService<AgentWorker>();
builder.Build().Run();
