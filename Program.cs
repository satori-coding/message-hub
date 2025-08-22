using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using MessageHub;
using MessageHub.Channels.Smpp;
using MessageHub.Channels.Http;
using MessageHub.Channels.Shared;
using MessageHub.Services;
using MassTransit;

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

// Add Entity Framework with environment-specific database providers
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (builder.Environment.IsDevelopment())
    {
        // Development: SQLite for local development
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
    else if (builder.Environment.EnvironmentName == "Test")
    {
        // Test: Azure SQL Database for WebApp hosting
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
    else
    {
        // Production: Azure SQL Database
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
});

// Add SMPP Channel services (needed by TenantChannelManager)
builder.Services.AddSmppChannel(builder.Configuration);

// Add HTTP SMS Channel services (needed by TenantChannelManager)
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

// Add Multi-Tenant Services
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<ITenantChannelManager, TenantChannelManager>();

// Add Message Service
builder.Services.AddScoped<MessageService>();

// Add HTTP Client Factory for tenant channels
builder.Services.AddHttpClient();

// Add MassTransit with RabbitMQ for async message processing
builder.Services.AddMassTransit(x =>
{
    // Add the MessageWorker consumer
    x.AddConsumer<MessageWorker>();
    
    // Configure RabbitMQ transport
    x.UsingRabbitMq((context, cfg) =>
    {
        // Get RabbitMQ connection settings
        var connectionString = builder.Configuration.GetConnectionString("RabbitMQ") 
                              ?? "amqp://guest:guest@localhost:5672/";
        
        cfg.Host(connectionString);
        
        // Configure the SMS queue with retry policy
        cfg.ReceiveEndpoint("sms-queue", e =>
        {
            // Retry policy: 3 retries with 5-second intervals
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
            
            // Configure the MessageWorker consumer
            e.ConfigureConsumer<MessageWorker>(context);
        });
    });
});

// Add Background Services
// MessageCleanupService removed - DLR handling now done immediately when ExpectDeliveryReceipts=false
// builder.Services.AddHostedService<MessageHub.Services.MessageCleanupService>();

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

// Environment-specific database initialization
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        logger.LogInformation("Initializing database for environment: {Environment}", app.Environment.EnvironmentName);
        
        if (app.Environment.IsDevelopment())
        {
            // Development (Azure WebApp): Fresh database on every startup
            logger.LogInformation("Creating fresh database for Development environment (Azure WebApp)");
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            await SeedDatabaseAsync(context, logger);
            
            // Seed tenants from configuration if multi-tenant mode is enabled
            await TenantSeedingService.SeedTenantsFromConfigurationAsync(context, builder.Configuration, logger);
            
            logger.LogInformation("Fresh SQLite database created and seeded");
        }
        else
        {
            // Local/Production (Linux Laptop): Persistent database with migrations  
            logger.LogInformation("Ensuring persistent database for Local environment (Linux Laptop)");
            await context.Database.EnsureCreatedAsync();
            
            // Seed tenants from configuration if multi-tenant mode is enabled and no tenants exist
            await TenantSeedingService.SeedTenantsFromConfigurationAsync(context, builder.Configuration, logger);
            
            logger.LogInformation("SQLite database ensured at: {DatabasePath}", 
                Path.Combine(Directory.GetCurrentDirectory(), "sms_database.db"));
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

/// <summary>
/// Seeds the database with demo data showcasing MessageParts architecture
/// </summary>
static async Task SeedDatabaseAsync(ApplicationDbContext context, ILogger logger)
{
    logger.LogInformation("Seeding database with demo MessageParts data...");
    
    // Demo Message 1: Single-part SMS (HTTP-style)
    var singleMessage = new Message
    {
        Recipient = "+49111222333",
        Content = "Welcome to MessageHub! This is a single SMS.",
        Status = MessageStatus.Delivered,
        ChannelType = ChannelType.HTTP,
        ProviderName = "DemoProvider",
        ProviderMessageId = "HTTP_001",
        MessageParts = 1,
        CreatedAt = DateTime.UtcNow.AddMinutes(-15),
        SentAt = DateTime.UtcNow.AddMinutes(-14),
        DeliveredAt = DateTime.UtcNow.AddMinutes(-13),
        UpdatedAt = DateTime.UtcNow.AddMinutes(-13),
        ChannelData = "{\"ChannelType\":\"HTTP\",\"Provider\":\"DemoProvider\"}"
    };
    context.Messages.Add(singleMessage);
    await context.SaveChangesAsync();
    
    // Demo Message 2: Multi-part SMS with MessageParts (SMPP-style)
    var multiMessage = new Message
    {
        Recipient = "+49444555666",
        Content = "This is a very long demonstration message that showcases our new MessageParts architecture. Each SMS part gets its own MessagePart record with individual delivery tracking. This allows us to see exactly which parts were delivered successfully and which ones failed. The parent message status is computed from all parts: if all parts are delivered, the message is delivered. If some parts fail, we get PartiallyDelivered status. This is professional-grade SMS service architecture similar to Twilio and AWS SNS.",
        Status = MessageStatus.PartiallyDelivered,
        ChannelType = ChannelType.SMPP,
        ProviderName = "SMPP",
        ProviderMessageId = "DEMO_100", // Primary part ID
        MessageParts = 4,
        CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        SentAt = DateTime.UtcNow.AddMinutes(-9),
        UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
        ChannelData = "{\"ChannelType\":\"SMPP\",\"SmppMessageIds\":[\"DEMO_100\",\"DEMO_101\",\"DEMO_102\",\"DEMO_103\"],\"MessageParts\":4}"
    };
    context.Messages.Add(multiMessage);
    await context.SaveChangesAsync();
    
    // Create MessageParts for the multi-part message
    var messageParts = new[]
    {
        new MessagePart
        {
            MessageId = multiMessage.Id,
            ProviderMessageId = "DEMO_100",
            PartNumber = 1,
            TotalParts = 4,
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow.AddMinutes(-9),
            SentAt = DateTime.UtcNow.AddMinutes(-9),
            DeliveredAt = DateTime.UtcNow.AddMinutes(-8),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-8),
            DeliveryStatus = "DELIVRD",
            DeliveryReceiptText = "id:DEMO_100 sub:001 dlvrd:001 submit date:2024 done date:2024 stat:DELIVRD"
        },
        new MessagePart
        {
            MessageId = multiMessage.Id,
            ProviderMessageId = "DEMO_101",
            PartNumber = 2,
            TotalParts = 4,
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow.AddMinutes(-9),
            SentAt = DateTime.UtcNow.AddMinutes(-9),
            DeliveredAt = DateTime.UtcNow.AddMinutes(-7),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-7),
            DeliveryStatus = "DELIVRD",
            DeliveryReceiptText = "id:DEMO_101 sub:001 dlvrd:001 submit date:2024 done date:2024 stat:DELIVRD"
        },
        new MessagePart
        {
            MessageId = multiMessage.Id,
            ProviderMessageId = "DEMO_102",
            PartNumber = 3,
            TotalParts = 4,
            Status = MessageStatus.Failed,
            CreatedAt = DateTime.UtcNow.AddMinutes(-9),
            SentAt = DateTime.UtcNow.AddMinutes(-9),
            DeliveredAt = DateTime.UtcNow.AddMinutes(-6),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-6),
            DeliveryStatus = "UNDELIV",
            DeliveryReceiptText = "id:DEMO_102 sub:001 dlvrd:000 submit date:2024 done date:2024 stat:UNDELIV err:003",
            ErrorCode = 3
        },
        new MessagePart
        {
            MessageId = multiMessage.Id,
            ProviderMessageId = "DEMO_103",
            PartNumber = 4,
            TotalParts = 4,
            Status = MessageStatus.Sent,
            CreatedAt = DateTime.UtcNow.AddMinutes(-9),
            SentAt = DateTime.UtcNow.AddMinutes(-9),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-9)
            // No delivery receipt yet - still pending
        }
    };
    
    context.MessageParts.AddRange(messageParts);
    await context.SaveChangesAsync();
    
    logger.LogInformation("Database seeded successfully with {MessageCount} messages and {PartCount} message parts", 
        2, messageParts.Length);
    logger.LogInformation("Demo data showcases: Single SMS, Multi-part SMS with mixed delivery status");
}