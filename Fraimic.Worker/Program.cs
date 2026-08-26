using Fraimic.Core;
using Fraimic.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Machine-specific settings (real Mongo credentials) live in appsettings.Local.json, which is
// gitignored — appsettings.json ships only safe defaults. Env vars (FraimicMuse__*) also work.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("FraimicMuse"));
var opt = builder.Configuration.GetSection("FraimicMuse").Get<WorkerOptions>()
    ?? throw new InvalidOperationException("Missing FraimicMuse configuration.");

builder.Services.AddSingleton(new JobRepository(opt.MongoConnectionString, opt.Database));

// Local generation stack — HTTP clients to Ollama and ComfyUI on this machine.
builder.Services.AddHttpClient<IPromptEnhancer, OllamaPromptEnhancer>(c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient<IImageGenerator, ComfyUiImageGenerator>(c => c.Timeout = TimeSpan.FromSeconds(360));
builder.Services.AddHttpClient<SafetyScreen>(c => c.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddHostedService<PipelineWorker>();

// On Windows this lets `sc.exe create` run it as a service; harmless when run from a console.
builder.Services.AddWindowsService(o => o.ServiceName = "FraimicMuseWorker");

var host = builder.Build();
host.Run();
