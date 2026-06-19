using Relisten.UserApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRelistenUserApi(builder.Configuration);

var app = builder.Build();
app.UseRelistenUserApi();
app.Run();

public partial class Program;
