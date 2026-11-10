using Fleet.SecretProxy;

try
{
    var app = SecretProxyApp.Build(args);
    app.Run();
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                           or System.Security.Cryptography.CryptographicException)
{
    // A broken CA or an invalid setting: say why and exit non-zero, rather than come up half working.
    Console.Error.WriteLine("fleet-secrets cannot start: " + ex.Message);
    Environment.Exit(1);
}
