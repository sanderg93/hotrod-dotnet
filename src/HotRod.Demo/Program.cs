using HotRod.Client;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 11222;
string cacheName = args.Length > 2 ? args[2] : "";
string? username = args.Length > 3 ? args[3] : null;
string? password = args.Length > 4 ? args[4] : null;
SaslMechanism mechanism = args.Length > 5 ? Enum.Parse<SaslMechanism>(args[5], ignoreCase: true) : SaslMechanism.ScramSha256;
TlsOptions? tls = args.Length > 6 && args[6].StartsWith("tls", StringComparison.OrdinalIgnoreCase)
    ? new TlsOptions { AllowUntrusted = args[6].Equals("tls-insecure", StringComparison.OrdinalIgnoreCase) }
    : null;

string auth = username is null ? "no auth" : $"SASL {mechanism} as '{username}'";
string transport = tls is null ? "plaintext" : tls.AllowUntrusted ? "TLS (no cert check)" : "TLS";
Console.WriteLine($"Connecting to {host}:{port} (cache: '{cacheName}', {auth}, {transport})...");

await using HotRodClient client = await HotRodClient.ConnectAsync(host, port, username, password, mechanism, tls);
RemoteCache cache = client.GetCache(cacheName);

await cache.PutAsync("Stad", "Amsterdam");
await cache.PutAsync("Land", "Nederland");
Console.WriteLine("Stored Stad=Amsterdam, Land=Nederland");

Console.WriteLine($"Get Stad         -> {await cache.GetAsync("Stad")}");
Console.WriteLine($"Get Onbekend     -> {await cache.GetAsync("Onbekend") ?? "(null)"}");
Console.WriteLine($"ContainsKey Land -> {await cache.ContainsKeyAsync("Land")}");
Console.WriteLine($"Size             -> {await cache.SizeAsync()}");

Console.WriteLine($"Remove Stad      -> {await cache.RemoveAsync("Stad")}");
Console.WriteLine($"ContainsKey Stad -> {await cache.ContainsKeyAsync("Stad")}");

await cache.ClearAsync();
Console.WriteLine($"After clear size -> {await cache.SizeAsync()}");

Console.WriteLine($"Cluster servers  -> {string.Join(", ", client.Servers)}");
