using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmmGab.Application.Abstractions;
using SmmGab.Background;
using SmmGab.Data;
using SmmGab.Domain.Models;
using SmmGab.Infrastructure.Connectors;
using SmmGab.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
var connectionString = builder.Configuration.GetConnectionString("Default");

// Ensure database exists before wiring DbContext
CreateDatabaseIfNotExists(connectionString);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddIdentity<User, IdentityRole<Guid>>(options =>
{
    // Password settings
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    
    // User settings
    options.User.RequireUniqueEmail = true;
    
    // Sign in settings
    options.SignIn.RequireConfirmedEmail = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/Login";
});

builder.Services.AddControllersWithViews()
    .AddNewtonsoftJson();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(24);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure options
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("RetryOptions"));

// Register services
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IDeltaFileExtractor, DeltaFileExtractor>();
builder.Services.AddScoped<IPublisherFactory, PublisherFactory>();

// Register HttpClient for publishers
builder.Services.AddHttpClient();

// Register background service
builder.Services.AddHostedService<PublicationSchedulerService>();

var app = builder.Build();

// Ensure database is created and migrations are applied
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var logger = services.GetRequiredService<ILogger<Program>>();
        
        // Проверяем подключение к БД
        var canConnect = await context.Database.CanConnectAsync();
        if (!canConnect)
        {
            logger.LogWarning("Cannot connect to database. Please check your connection string.");
        }
        else
        {
            logger.LogInformation("Applying database migrations...");
            await context.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully.");
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database.");
        // Не останавливаем приложение, если миграции не удались - возможно БД еще не готова
    }
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseSession();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Creates the PostgreSQL database if it does not exist.
static void CreateDatabaseIfNotExists(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Connection string 'Default' is not configured.");
    }

    var builder = new NpgsqlConnectionStringBuilder(connectionString);

    // Connect to the maintenance database to issue CREATE DATABASE
    var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
    {
        Database = "postgres"
    };

    using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
    connection.Open();

    using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT 1 FROM pg_database WHERE datname = @dbName";
    cmd.Parameters.AddWithValue("dbName", builder.Database);

    var exists = cmd.ExecuteScalar() is not null;
    if (!exists)
    {
        cmd.CommandText = $"CREATE DATABASE \"{builder.Database}\"";
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();
    }
}

app.Run();
