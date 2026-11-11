using RelistenUserService;

var app = AccountsApplication.Build(WebApplication.CreateBuilder(args));
await AccountsApplication.InitializeAsync(app, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;
