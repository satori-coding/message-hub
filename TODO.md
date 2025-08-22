# MessageHub Async Processing with MassTransit & Service Bus

## 🚀 **Async Processing Architecture**

MessageHub implements **queue-based asynchronous message processing** using MassTransit with environment-specific transports:

### **Architecture Benefits**
- ✅ **Immediate API Response**: POST returns with `statusUrl` immediately
- ✅ **Background Processing**: SMS sending happens asynchronously in background
- ✅ **Scalability**: Message queues handle high-volume SMS processing
- ✅ **Reliability**: Message persistence and retry logic
- ✅ **Multi-Environment**: RabbitMQ (local) + Azure Service Bus (cloud)

### **API Response Format**
```json
{
  "id": 123,
  "status": "Queued for processing", 
  "statusUrl": "https://api.messagehub.com/api/message/123/status",
  "message": "Message queued successfully"
}
```

## 🌍 **Environment-Specific Configuration**

### **Local Environment** (RabbitMQ)
```json
"MassTransitSettings": {
  "Transport": "RabbitMQ",
  "RabbitMQ": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "QueueName": "sms-processing-local"
  }
}
```

**Setup**: `./scripts/start-rabbitmq.sh` (Docker-based)

### **Development Environment** (Azure Service Bus)
```json
"MassTransitSettings": {
  "Transport": "AzureServiceBus",
  "AzureServiceBus": {
    "ConnectionString": "Endpoint=sb://your-dev-servicebus.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-dev-key",
    "QueueName": "sms-processing-dev"
  }
}
```

### **Test/Production Environment** (Azure Service Bus)
```json
"MassTransitSettings": {
  "Transport": "AzureServiceBus",
  "AzureServiceBus": {
    "ConnectionString": "Endpoint=sb://your-prod-servicebus.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-prod-key",
    "QueueName": "sms-processing-prod"
  }
}
```

## 📦 **Required NuGet Packages**

```xml
<PackageReference Include="MassTransit" Version="8.1.3" />
<PackageReference Include="MassTransit.RabbitMQ" Version="8.1.3" />
<PackageReference Include="MassTransit.Azure.ServiceBus.Core" Version="8.1.3" />
```

## 🏗️ **Implementation Components**

### **Message Contract**
```csharp
public record SendMessageCommand(
    string PhoneNumber,
    string Content, 
    ChannelType ChannelType,
    int? TenantId,
    string? ChannelName,
    int MessageId); // DB Message ID
```

### **Message Consumer**
```csharp
public class SendMessageConsumer : IConsumer<SendMessageCommand>
{
    public async Task Consume(ConsumeContext<SendMessageCommand> context)
    {
        // Process SMS sending asynchronously
        // Update message status in database
        // Handle retries and error cases
    }
}
```

### **Controller Changes**
```csharp
[HttpPost("send")]
public async Task<ActionResult<MessageResponse>> SendMessage([FromBody] SendMessageRequest request)
{
    // Create message in DB with "Queued" status
    var message = await _messageService.CreateMessageAsync(request);
    
    // Queue message for background processing
    await _publishEndpoint.Publish(new SendMessageCommand(...));
    
    // Return immediate response with status URL
    return Ok(new MessageResponse
    {
        Id = message.Id,
        Status = "Queued for processing",
        StatusUrl = $"/api/message/{message.Id}/status",
        Message = "Message queued successfully"
    });
}
```

## 🛠️ **Development Scripts**

### **Start RabbitMQ (Local)**
```bash
# Create script: scripts/start-rabbitmq.sh
#!/bin/bash
docker run -d --name messagequeue-rabbitmq \
  -p 15672:15672 -p 5672:5672 \
  masstransit/rabbitmq

echo "RabbitMQ Management UI: http://localhost:15672 (guest/guest)"
```

### **Service Bus Simulation**
For local development without Azure access, RabbitMQ provides full Service Bus simulation:
- Message persistence
- Dead letter queues
- Retry mechanisms
- Management UI

## 🎯 **Next Implementation Steps**

1. **Add MassTransit NuGet packages**
2. **Create message contracts and consumers**
3. **Update Program.cs with MassTransit configuration**
4. **Modify MessageController for async processing**
5. **Create RabbitMQ startup script**
6. **Test with all three environments**

## 📊 **Monitoring & Observability**

- **RabbitMQ Management**: http://localhost:15672 (local)
- **Azure Service Bus Metrics**: Azure Portal monitoring
- **Application Insights**: Message processing telemetry
- **Database Status Tracking**: Real-time message status updates

---

**Status**: Architecture planned and documented. Ready for implementation when async processing is needed.