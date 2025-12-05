using RemotePCControl.WebInterface;
using RemotePCControl.WebInterface.Services;
using RemotePCControl.WebInterface.Hubs;

var builder = WebApplication.CreateBuilder(args);

string serverIp = Environment.GetEnvironmentVariable("REMOTEPC_SERVER_IP");

if (!string.IsNullOrWhiteSpace(serverIp))
{
    builder.WebHost.UseUrls($"http://{serverIp}:5000");
}


// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.Configure<ServerConnectionOptions>(
    builder.Configuration.GetSection(ServerConnectionOptions.SectionName));
builder.Services.AddSingleton<ConnectionService>();
builder.Services.AddSignalR();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();
app.MapHub<ControlHub>("/controlHub");
app.MapRazorPages();

app.Run();
