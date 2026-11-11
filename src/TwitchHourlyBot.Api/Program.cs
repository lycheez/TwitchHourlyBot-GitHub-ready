using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TwitchHourlyBot.Api;
using TwitchHourlyBot.Api.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=twitchbot.db"));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("TwitchHourlyBot");
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddHttpClient("twitch");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<DbCooldownStore>();
builder.Services.AddSingleton<TwitchSession>();
builder.Services.AddSingleton<IKeywordMonitorFactory, EventSubKeywordMonitorFactory>();
builder.Services.AddSingleton<BotRuntime>();
builder.Services.AddHostedService<BotWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDbContext>().EnsureSchema();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.MapApi();
app.MapBotApi();
app.Run();

public partial class Program { }
