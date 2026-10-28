using Fleet.StockIdentity;

var builder = WebApplication.CreateBuilder(args);
var path = builder.Configuration["StockOpenShell:AuthorityFile"];
if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
    throw new InvalidOperationException("stock_authority_file_required");
builder.Services.AddSingleton(new StockAuthorityFile(path));
builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 1048576; options.MaxSendMessageSize = 1048576; });
var app = builder.Build();
app.MapGrpcService<StockSupervisorMiddleware>();
app.Run();
