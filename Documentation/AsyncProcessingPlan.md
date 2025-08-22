# MassTransit Queue-basierte MessageHub Architektur + Status URL Response

## 🎯 **Ziel**: Asynchrone Message-Verarbeitung mit sofortiger Status-URL Response

**Neue API Response**:
```json
{
  "id": 123,
  "status": "Queued for processing", 
  "statusUrl": "https://api.messagehub.com/api/message/123/status",
  "message": "Message queued successfully"
}
```

## 📦 **Phase 1: MassTransit Setup & Lokale Entwicklung**

### NuGet Packages
- `MassTransit` (Core)
- `MassTransit.RabbitMQ` (für lokale Entwicklung) 
- `MassTransit.Azure.ServiceBus.Core` (für Produktion)

### Lokale Entwicklungsoptionen
1. **RabbitMQ Docker** (Empfohlen):
   ```bash
   docker run -p 15672:15672 -p 5672:5672 masstransit/rabbitmq
   ```
   - Management UI: http://localhost:15672 (guest/guest)
   - Vollständige Message-Persistierung

2. **In-Memory Transport** (Tests):
   - Schnell für Unit Tests
   - Keine Docker-Abhängigkeit

### Script: `scripts/start-rabbitmq.sh`
- Analog zu `start-smppsim.sh`
- Startet RabbitMQ Container für lokale Entwicklung

## 🏗️ **Phase 2: Architektur-Umbau mit Status URL**

### Message Contracts
```csharp
public record SendMessageCommand(
    string PhoneNumber,
    string Content, 
    ChannelType ChannelType,
    int? TenantId,
    string? ChannelName,
    int MessageId); // DB Message ID
```

### Enhanced Controller Response (`MessageController.cs`)
```csharp
[HttpPost("send")]
public async Task<ActionResult<SendMessageResponse>> SendMessage([FromBody] SendMessageRequest request)
{
    // 1. Message in DB erstellen (Status: Queued)
    var message = await _messageService.CreateMessageAsync(request); // NEUE Methode
    
    // 2. Command in Queue stellen
    await _bus.Publish(new SendMessageCommand(...));
    
    // 3. Status URL generieren
    var statusUrl = $"{Request.Scheme}://{Request.Host}/api/message/{message.Id}/status";
    
    // 4. Sofortige Response mit Status URL
    return Ok(new SendMessageResponse 
    { 
        Id = message.Id,
        PhoneNumber = message.Recipient,
        Status = "Queued",
        StatusUrl = statusUrl,  // ← NEU: Direkte Status-Abfrage URL
        CreatedAt = message.CreatedAt,
        Message = "Message queued for processing"
    });
}
```

### Enhanced SendMessageResponse Model
```csharp
public class SendMessageResponse
{
    public int Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusUrl { get; set; } = string.Empty;  // ← NEU
    public DateTime CreatedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}
```

### MessageService Erweiterung
```csharp
// NEUE Methode für Queue-Pattern
public async Task<Message> CreateMessageAsync(SendMessageRequest request, int? tenantId = null)
{
    var message = new Message
    {
        Recipient = request.PhoneNumber,
        Content = request.Content,
        Status = MessageStatus.Queued,  // ← Neuer Status
        ChannelType = request.ChannelType ?? ChannelType.SMPP,
        TenantId = tenantId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    _dbContext.Messages.Add(message);
    await _dbContext.SaveChangesAsync();
    
    return message;
}
```

### Background Message Worker
```csharp
public class MessageWorker : IConsumer<SendMessageCommand>
{
    public async Task Consume(ConsumeContext<SendMessageCommand> context)
    {
        var command = context.Message;
        
        // Status von Queued auf Pending ändern
        await _messageService.UpdateMessageStatusAsync(command.MessageId, MessageStatus.Pending);
        
        // Existierende SendMessageAsync() verwenden
        await _messageService.SendMessageAsync(command.MessageId, command.ChannelName);
    }
}
```

## 🔄 **Phase 3: Status Lifecycle**

### Erweiterte MessageStatus Enum
```csharp
public enum MessageStatus
{
    Queued,           // ← NEU: In Queue wartet auf Verarbeitung
    Pending,          // Worker verarbeitet gerade
    Sent,             // An Provider gesendet, wartet auf DLR
    Failed,           // Fehler beim Senden
    Delivered,        // Erfolgreich zugestellt
    AssumedDelivered, // Timeout, vermutlich zugestellt
    // ... andere Status
}
```

### Status Display Logic
```csharp
private string GetDisplayStatus(Message message)
{
    return message.Status switch
    {
        MessageStatus.Queued => "Queued for processing",
        MessageStatus.Pending => "Processing...",
        MessageStatus.Sent => "Sent (awaiting delivery confirmation)",
        MessageStatus.AssumedDelivered => "Assumed Delivered (no DLR received)",
        MessageStatus.Delivered => "Delivered (confirmed)",
        _ => message.Status.ToString()
    };
}
```

## 🚀 **Phase 4: Client Usage Pattern**

### Typischer Client-Workflow
```javascript
// 1. SMS senden
const response = await fetch('/api/message/send', {
    method: 'POST',
    body: JSON.stringify({
        phoneNumber: '+49123456789',
        content: 'Hello World!'
    })
});

const result = await response.json();
// result.statusUrl = "https://api.../api/message/123/status"

// 2. Status polling mit der URL
const checkStatus = async () => {
    const statusResponse = await fetch(result.statusUrl);
    const status = await statusResponse.json();
    console.log(status.status); // "Queued" → "Processing..." → "Sent" → "Delivered"
};

// 3. Polling alle 5 Sekunden
setInterval(checkStatus, 5000);
```

## 🔧 **Vorteile der URL-basierten Status-Abfrage**

### Developer Experience
- ✅ **Self-Contained Response**: Client bekommt alles was er braucht
- ✅ **No URL Construction**: Client muss keine URLs zusammenbauen
- ✅ **RESTful Design**: Folgt REST-Prinzipien mit Resource-URLs

### Multi-Tenant Support
- ✅ **Tenant-Aware URLs**: StatusUrl funktioniert automatisch mit X-Subscription-Key
- ✅ **Sichere Zugriffe**: Nur berechtigte Tenants können Status abfragen

### Production Ready
- ✅ **Environment Agnostic**: URLs passen sich automatisch an (localhost/staging/prod)
- ✅ **HTTPS Support**: Verwendet aktuelles Request-Schema

## 📋 **Implementierungsschritte**

1. **Docker RabbitMQ Setup** + Start-Script
2. **MassTransit Packages** hinzufügen  
3. **MessageStatus.Queued** hinzufügen
4. **SendMessageResponse.StatusUrl** hinzufügen
5. **MessageService.CreateMessageAsync()** implementieren
6. **MessageWorker** implementieren
7. **Controller** auf Queue+URL Pattern umstellen
8. **Status Display Logic** erweitern
9. **Testing** mit lokaler RabbitMQ-Instanz

**Geschätzte Entwicklungszeit**: 2-3 Tage für vollständige Implementation

**Ergebnis**: Moderne asynchrone SMS-API mit sofortiger Response und selbst-beschreibenden Status-URLs!

## 📊 **Architektur-Vergleich**

### Vorher (Synchron)
```
API Request → MessageService → Channel → Provider → Response (200-500ms)
```

### Nachher (Asynchron)
```
API Request → Queue → Response (~10ms)
           ↓
Background Worker → Channel → Provider → DB Update
```

## 🛡️ **Error Handling & Reliability**

### Message Retry Policy
```csharp
services.AddMassTransit(x => {
    x.UsingRabbitMq((context, cfg) => {
        cfg.ReceiveEndpoint("sms-queue", e => {
            e.UseRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
            e.ConfigureConsumer<MessageWorker>(context);
        });
    });
});
```

### Dead Letter Queue
- Failed messages nach 3 Retry-Versuchen
- Manuelle Intervention möglich
- Monitoring und Alerting

### Single Queue FIFO (wie gewünscht)
- Eine Queue für alle Tenants
- First-In-First-Out Verarbeitung  
- Tenant-Isolation nur auf Datenebene

## 🏃‍♂️ **Performance Benefits**

### API Response Zeit
- **Vorher**: 200-500ms (SMPP-abhängig)
- **Nachher**: ~10ms (nur DB + Queue)

### Durchsatz
- **Vorher**: 1 Message/Request (blockierend)
- **Nachher**: Unlimited Queue + Background Processing

### Skalierbarkeit
- **Horizontal**: Mehrere Worker-Instanzen
- **Vertikal**: Queue-Größe nach Bedarf