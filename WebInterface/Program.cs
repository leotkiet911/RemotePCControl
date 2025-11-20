using RemotePCControl.WebInterface;
using RemotePCControl.WebInterface.Services;
using RemotePCControl.WebInterface.Hubs;

var builder = WebApplication.CreateBuilder(args);

var urlConfig = builder.Configuration.GetSection("WebHost")?.GetValue<string>("Urls");
if (!string.IsNullOrWhiteSpace(urlConfig))
{
    var urls = urlConfig
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (urls.Length > 0)
    {
        builder.WebHost.UseUrls(urls);
    }
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
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
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
