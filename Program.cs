using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using MessageHub;
using MessageHub.Channels.Smpp;
using MessageHub.Channels.Http;
using MessageHub.Channels.Shared;

var builder = WebApplication.CreateBuilder(args);

// Add Azure Key Vault configuration
if (!builder.Environment.IsDevelopment())
{
    var keyVaultEndpoint = builder.Configuration["KeyVaultEndpoint"];
    if (!string.IsNullOrEmpty(keyVaultEndpoint))
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri(keyVaultEndpoint),
            new DefaultAzureCredential(),
            new AzureKeyVaultConfigurationOptions());
    }
}

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add Application Insights
builder.Services.AddApplicationInsightsTelemetry();

// Add Entity Framework with SQLite for local development
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
    else
    {
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
});

// Add SMPP Channel services
builder.Services.AddSmppChannel(builder.Configuration);

// Add HTTP SMS Channel services (with default test configuration)
var httpSmsConfig = MessageHub.Channels.Http.HttpSmsProviderTemplates.Generic(
    "TestProvider",
    "https://api.test-sms-provider.com/send", 
    "test-api-key",
    "TestSender"
);
builder.Services.AddHttpSmsChannel(httpSmsConfig);

// Register all IMessageChannel implementations for multi-channel routing
builder.Services.AddScoped<IEnumerable<IMessageChannel>>(serviceProvider =>
{
    var channels = new List<IMessageChannel>();
    
    // Add SMPP channel
    var smppChannel = serviceProvider.GetRequiredService<ISmppChannel>();
    channels.Add((IMessageChannel)smppChannel);
    
    // Add HTTP channel
    var httpChannel = serviceProvider.GetRequiredService<MessageHub.Channels.Http.HttpSmsChannel>();
    channels.Add(httpChannel);
    
    return channels;
});

// Add Message Service
builder.Services.AddScoped<MessageService>();

var app = builder.Build();

// Configure SMPP channel delivery receipt handling
using (var scope = app.Services.CreateScope())
{
    var smppChannel = scope.ServiceProvider.GetRequiredService<ISmppChannel>();
    var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
    
    // Subscribe to delivery receipt events
    smppChannel.OnDeliveryReceiptReceived += receipt =>
    {
        // Process delivery receipt asynchronously to avoid blocking SMPP channel
        Task.Run(async () =>
        {
            try
            {
                using var serviceScope = app.Services.CreateScope();
                var scopedMessageService = serviceScope.ServiceProvider.GetRequiredService<MessageService>();
                await scopedMessageService.ProcessDeliveryReceiptAsync(receipt);
            }
            catch (Exception ex)
            {
                var logger = app.Services.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Error processing delivery receipt for SMPP message ID: {SmppMessageId}", 
                    receipt.SmppMessageId);
            }
        });
    };
    
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("SMPP channel delivery receipt handling configured successfully");
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Ensure database is created and migrated
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        logger.LogInformation("Initializing database...");
        
        if (app.Environment.IsDevelopment())
        {
            // For SQLite, ensure database is created
            await context.Database.EnsureCreatedAsync();
            logger.LogInformation("SQLite database ensured at: {DatabasePath}", 
                Path.Combine(Directory.GetCurrentDirectory(), "sms_database.db"));
        }
        else
        {
            // For production, use migrations
            await context.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully");
        }
        
        // Test database connection
        var canConnect = await context.Database.CanConnectAsync();
        if (canConnect)
        {
            logger.LogInformation("Database connection successful");
        }
        else
        {
            logger.LogError("Database connection failed");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while initializing the database");
        throw;
    }
}

// Test SMPP channel health
using (var scope = app.Services.CreateScope())
{
    var smppChannel = scope.ServiceProvider.GetRequiredService<ISmppChannel>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        var isHealthy = await smppChannel.IsHealthyAsync();
        if (isHealthy)
        {
            logger.LogInformation("SMPP channel is healthy and ready");
        }
        else
        {
            logger.LogWarning("SMPP channel health check failed - connections may be created on demand");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error checking SMPP channel health");
    }
}

app.Run();