using Fleet.Comms;
using Microsoft.AspNetCore.Builder;

// The north listener. The south surface is a separate listener and is not part of this slice;
// nothing here maps a south route, and nothing here exposes a placeholder for one.
//
// Listener addresses, certificates and any credential come from the host environment at run time.
// None of them is in this repository.
var app = CommsApp.BuildNorthApp(WebApplication.CreateBuilder(args));
await app.RunAsync();

/// <summary>Named so the test host can reference the entry-point assembly.</summary>
public partial class Program;
