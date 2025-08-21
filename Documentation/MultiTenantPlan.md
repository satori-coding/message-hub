# Multi-Tenant SMPP Architecture Plan

## 📋 **Analyse der aktuellen Architektur**

### Aktuelle Limitierungen (Single-Tenant)
- **SmppChannel als Singleton**: Eine globale Instanz für alle Requests
- **Hardcodierte Provider-Konfiguration**: Nur ein SMPP-Provider aus appsettings.json
- **Shared Connection Pool**: Alle Requests teilen dieselben 3 SMPP-Verbindungen
- **Keine Tenant-Isolation**: Unmöglich, verschiedene SMPP-Provider pro Tenant zu verwenden
- **DLR-Event-Routing**: Delivery Receipts können nicht tenant-spezifisch verarbeitet werden

### Probleme bei 10 Tenants mit verschiedenen SMPP-Providern
1. **Provider-Konflikte**: Verschiedene Tenants benötigen verschiedene SMPP-Server (Host, Port, Credentials)
2. **Connection Pool Sharing**: Tenant A und B würden dieselben Verbindungen verwenden
3. **DLR-Zuordnung**: Delivery Receipts von Provider A könnten fälschlicherweise Tenant B zugeordnet werden
4. **Performance-Isolation**: Ein langsamer Provider würde alle Tenants beeinträchtigen
5. **Konfigurationskomplexität**: Unmöglich, pro-Tenant SMPP-Einstellungen zu verwalten

## ✅ **Inetlab.SMPP Multi-Provider Capabilities**

### Bestätigte Funktionen
- **Multiple SmppClient-Instanzen**: Vollständige Unterstützung für parallele SMPP-Verbindungen
- **SmppRouterSample-Pattern**: Dokumentiertes Multi-Provider-Management
- **Asynchrone Architektur**: Task-based asynchronous pattern mit hoher Stabilität
- **Separate Event-Handler**: Jeder SmppClient kann eigene DLR-Events verarbeiten
- **Connection Mode Flexibilität**: Transmitter, Receiver, Transceiver pro Provider

### Architektur-Empfehlung von Inetlab
```csharp
// Jeder Tenant = eigener SmppClient
List<SmppClient> tenantClients = new List<SmppClient>();

// Pro Provider separate Session  
await router.AddSession(endpoint, systemId, password);

// Tenant-spezifisches Message-Routing
SubmitSmResp[] resp = await router.SubmitAsync(sms, 
    client => client.SystemID == tenantSystemId);
```

## 🎯 **Multi-Tenant SMPP Architecture Design**

### Kern-Prinzipien
1. **Tenant-Isolation**: Jeder Tenant hat dedizierte SMPP-Verbindungen
2. **Provider-Flexibilität**: Jeder Tenant kann verschiedene SMPP-Provider verwenden
3. **Performance-Isolation**: Provider-Performance beeinflusst nur den jeweiligen Tenant
4. **Configuration-Separation**: Tenant-spezifische SMPP-Konfigurationen
5. **Event-Isolation**: DLR-Events werden korrekt tenant-spezifisch geroutet

### Architektur-Komponenten

#### 1. TenantSmppChannelManager
```csharp
public class TenantSmppChannelManager
{
    private readonly ConcurrentDictionary<string, SmppChannel> _tenantChannels;
    private readonly ITenantConfigurationService _configService;
    
    public async Task<SmppChannel> GetChannelForTenantAsync(string tenantId);
    public async Task<SmppChannel> CreateChannelForTenantAsync(string tenantId, SmppChannelConfiguration config);
    public async Task RemoveTenantChannelAsync(string tenantId);
}
```

#### 2. Multi-Tenant Configuration Model
```csharp
public class MultiTenantSmppConfiguration
{
    public string TenantId { get; set; }
    public string TenantName { get; set; }
    public SmppChannelConfiguration SmppConfig { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

#### 3. Tenant-Aware Message Model
```csharp
public class Message
{
    // Existing properties...
    public string TenantId { get; set; }  // NEW: Tenant-Zuordnung
    public string? TenantName { get; set; }  // NEW: Tenant-Name für Logging
}
```

## 🛠️ **Implementierungsplan**

### Phase 1: Multi-Tenant Infrastructure
1. **TenantSmppChannelManager erstellen**
   - Dictionary<TenantId, SmppChannel> für Provider-Isolation
   - Tenant-spezifische Connection Pools
   - Dynamic SmppChannel creation/disposal
   - Thread-safe Tenant-Channel-Management

2. **Tenant Configuration System**
   - `MultiTenantSmppConfiguration` Entity im Database-Model
   - Configuration Repository/Service für Tenant-Settings
   - Hot-reloading von Tenant-Konfigurationen aus Database/Key Vault
   - Validation für Tenant-spezifische SMPP-Settings

### Phase 2: Channel Architecture Refactoring
3. **SmppChannelFactory implementieren**
   - Factory Pattern für Tenant-spezifische Channels
   - Connection Pool Isolation per Tenant
   - Provider-specific configuration validation
   - Automatic Channel-Lifecycle-Management

4. **Tenant-Aware Message Routing**
   - TenantId in Message-Model integrieren
   - MessageService Routing-Logic erweitern für Multi-Tenant
   - Tenant-spezifische Channel-Selection
   - Multi-Provider DLR-Event-Handling mit Tenant-Zuordnung

### Phase 3: API & Service Layer Updates
5. **MessageService Refactoring**
   - Tenant-Context in SendAsync-Methods
   - Tenant-spezifische Channel-Resolution
   - Error-Handling per Tenant-Provider
   - Tenant-isolierte Performance-Metriken

6. **REST API Enhancement**
   - Tenant-Header in HTTP-Requests (X-Tenant-ID)
   - Tenant-aware Swagger-Documentation
   - Provider Selection per Request
   - Tenant-spezifische Rate-Limiting

### Phase 4: Configuration & Monitoring
7. **Database Schema für Multi-Tenancy**
   - TenantConfigurations-Tabelle
   - Tenant-Message-Relationships
   - Migration-Scripts für bestehende Daten

8. **Monitoring & Observability**
   - Tenant-spezifische Logging-Context
   - Provider-spezifische Performance-Metriken
   - Connection Usage Analytics per Tenant
   - Health-Check-Endpoints per Tenant-Provider

## 📊 **Expected Benefits**

### Business Value
- ✅ **Complete Tenant Isolation** mit separaten SMPP-Providern
- ✅ **Scalable Architecture** für 10+ Tenants ohne Performance-Einbußen
- ✅ **Provider-Specific Features** (verschiedene DLR-Handling, Timeouts)
- ✅ **Independent Performance** - Provider-Issues betreffen nur einen Tenant
- ✅ **Professional Multi-Tenant SMS Service** ready for enterprise deployment

### Technical Benefits
- ✅ **Resource Isolation**: Jeder Tenant hat dedizierte SMPP-Verbindungen
- ✅ **Configuration Flexibility**: Pro-Tenant SMPP-Provider-Konfiguration
- ✅ **Error Isolation**: Provider-Fehler sind tenant-spezifisch
- ✅ **Monitoring Granularity**: Detailed analytics per Tenant-Provider-Kombination
- ✅ **Horizontal Scalability**: Einfache Tenant-Addition ohne Service-Impact

## 🔧 **Implementierung Details**

### Tenant-Channel-Lifecycle
```csharp
// Tenant-Channel wird lazy erstellt beim ersten Request
var channel = await _tenantChannelManager.GetChannelForTenantAsync(tenantId);

// Channel-Configuration aus Database/KeyVault
var config = await _configService.GetSmppConfigurationAsync(tenantId);

// Dedicated Connection Pool pro Tenant
var tenantChannel = new SmppChannel(config, logger);
```

### DLR-Event-Routing
```csharp
// Jeder Tenant-Channel hat eigene DLR-Handler
tenantChannel.OnDeliveryReceiptReceived += receipt =>
{
    // Tenant-Context ist automatisch verfügbar
    await ProcessTenantDeliveryReceiptAsync(tenantId, receipt);
};
```

### Message-Send mit Tenant-Context
```csharp
public async Task<MessageResult> SendAsync(string tenantId, Message message)
{
    var channel = await _tenantChannelManager.GetChannelForTenantAsync(tenantId);
    return await channel.SendAsync(message);
}
```

## ⚠️ **Wichtige Überlegungen**

### Resource Management
- **Connection Limits**: Pro-Tenant Connection-Pool-Größen konfigurierbar
- **Memory Usage**: Monitoring für Tenant-Channel-Memory-Consumption
- **Idle Cleanup**: Automatic disposal von inaktiven Tenant-Channels

### Security
- **Tenant Isolation**: Sichere Trennung von Tenant-Daten und -Verbindungen
- **Configuration Security**: Sichere Speicherung von Tenant-SMPP-Credentials
- **Access Control**: Tenant kann nur eigene Messages und Konfigurationen zugreifen

### Performance
- **Connection Pooling**: Optimierte Pool-Größen basierend auf Tenant-Activity
- **Lazy Loading**: Tenant-Channels werden nur bei Bedarf erstellt
- **Health Monitoring**: Proactive Überwachung von Tenant-Provider-Health

## 📝 **Nächste Schritte**

1. **Database Schema Design** für Multi-Tenant-Konfigurationen
2. **TenantSmppChannelManager** Implementation
3. **Message-Model** um TenantId erweitern
4. **API-Layer** für Tenant-Context-Handling
5. **Testing Strategy** für Multi-Tenant-Szenarien

---

**Ziel**: Eine professionelle, hochskalierbare Multi-Tenant-SMS-Service-Architektur, die es ermöglicht, dass jeder Tenant seine eigenen SMPP-Provider verwenden kann, ohne andere Tenants zu beeinträchtigen.