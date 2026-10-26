using ClearForge.Ai;

// ClearForge AI worker — isolated local-inference process.
// Loads exactly one curated GGUF model from the dedicated model root,
// serves a tiny allowlisted NDJSON surface, and exits when the desktop
// closes stdin (orphan-proof, same convention as ClearForge.Host).
var modelRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ClearForge", "models");

await using var runtime = new LlmRuntime(modelRoot);
var dispatcher = new AiDispatcher(runtime);
var pipeName = args.FirstOrDefault() ?? AiPipeServer.DefaultPipeName;

await using var server = new AiPipeServer(dispatcher, pipeName);
server.Start();
Console.WriteLine($"clearforge-ai listening on \\\\.\\pipe\\{pipeName} (protocol v{ClearForge.Contracts.Protocol.Version})");

await Task.Run(() => { try { while (Console.In.Read() != -1) { } } catch { } });
