# Multi-Tenant SMS MessageHub - Testing Guide

## 🧪 **Development Multi-Tenant Testing Setup**

### **Konfigurationsdateien**

#### 1. **appsettings.DevelopmentMultiTenant.json** (Neu erstellt)
- **Zweck**: Lokale Multi-Tenant Entwicklung und Tests
- **Tenants**: 3 Test-Tenants mit unterschiedlichen Channel-Kombinationen
- **SMPP**: Nutzt localhost SMPP-Simulator auf Port 2775
- **HTTP**: Nutzt httpbin.org und postman-echo.com für HTTP-Channel Tests

#### 2. **appsettings.MultiTenant.Example.json** (Verbessert)
- **Zweck**: Produktions-Template mit Beispiel-Providern
- **Struktur**: "Tenants" statt "SampleTenants" (konsistenter Name)

### **Test-Tenants (Development)**

| Tenant | Subscription Key | SMPP Channel | HTTP Channel | Zweck |
|--------|-----------------|---------------|--------------|--------|
| **DevTenant_A** | `dev-tenant-a-12345` | localhost:2775 (tenant_a) | httpbin fallback | SMPP + HTTP Fallback |
| **DevTenant_B** | `dev-tenant-b-67890` | localhost:2775 (tenant_b) | - | Nur SMPP |
| **DevTenant_C** | `dev-tenant-c-http-99999` | - | httpbin + postman-echo | Nur HTTP Channels |

## 🚀 **Testing Workflow**

### **1. Multi-Tenant Mode aktivieren**
```bash
# Kopiere Development Multi-Tenant Konfiguration
cp appsettings.DevelopmentMultiTenant.json appsettings.Development.json

# Oder setze direkt in appsettings.Development.json:
{
  "MultiTenantSettings": {
    "EnableMultiTenant": true,
    "RequireSubscriptionKey": true
  }
}
```

### **2. SMPP Simulator starten**
```bash
# Docker-basierter SMPP Simulator
./scripts/start-smppsim.sh

# Verify SMPP simulator is running
# Web Interface: http://localhost:8088
# SMPP Port: localhost:2775
```

### **3. Application starten**
```bash
# Fresh database mit Tenant-Seeding
dotnet run

# Logs sollten zeigen:
# "Seeding 3 tenants from configuration..."
# "Created tenant: DevTenant_A (ID: 1)"
# "Created SMPP channel 'localhost-smpp' for tenant ID 1"
```

### **4. API Tests ausführen**

#### **VS Code REST Client** (Empfohlen)
```bash
# 1. Installiere "REST Client" Extension in VS Code
# 2. Öffne scripts/api-tests.http
# 3. Klicke auf "Send Request" über den Multi-Tenant Tests

# Beispiel API Requests:
```

#### **Curl Commands**
```bash
# Tenant A - SMPP Channel
curl -k -X POST "https://localhost:7142/api/message/send" \
  -H "X-Subscription-Key: dev-tenant-a-12345" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49555111222", "Content": "Tenant A SMPP Test!", "ChannelName": "localhost-smpp"}'

# Tenant B - SMPP Channel (anderer SystemId)
curl -k -X POST "https://localhost:7142/api/message/send" \
  -H "X-Subscription-Key: dev-tenant-b-67890" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49666222333", "Content": "Tenant B SMPP Test!", "ChannelName": "localhost-smpp-alt"}'

# Tenant C - HTTP Channel
curl -k -X POST "https://localhost:7142/api/message/send" \
  -H "X-Subscription-Key: dev-tenant-c-http-99999" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49777333444", "Content": "Tenant C HTTP Test!", "ChannelName": "httpbin-primary"}'
```

## 📊 **Test-Szenarien**

### **✅ Positive Tests**
- **Tenant Isolation**: Jeder Tenant sieht nur eigene Nachrichten
- **Channel Selection**: Tenant A kann zwischen SMPP und HTTP wählen
- **Default Channels**: Automatische Channel-Auswahl basierend auf Tenant-Konfiguration
- **Multi-Part SMS**: Lange Nachrichten über tenant-spezifische SMPP-Verbindungen

### **🚫 Security Tests**
- **Kein Subscription Key**: 401 Unauthorized in Multi-Tenant Mode
- **Ungültiger Key**: 401 Unauthorized
- **Tenant Isolation**: Tenant B kann nicht auf Tenant A Nachrichten zugreifen
- **Channel Access**: Tenant kann nur eigene konfigurierte Channels verwenden

### **⚠️ Error Handling Tests**
- **Nicht existierende Channels**: Graceful error handling
- **SMPP Connection Failures**: Timeout und Retry-Mechanismen
- **HTTP Provider Errors**: Robuste HTTP-Channel Fehlerbehandlung

## 🔍 **Debugging & Monitoring**

### **Database Inspection**
```bash
# Tenants anzeigen
python3 scripts/view_db.py
# oder
sqlite3 multitenant_dev_database.db "SELECT * FROM Tenants;"

# Tenant Channel Konfigurationen
sqlite3 multitenant_dev_database.db "SELECT * FROM TenantChannelConfigurations;"

# Nachrichten mit Tenant-Zuordnung
sqlite3 multitenant_dev_database.db "SELECT Id, TenantId, Recipient, Status, ChannelType FROM Messages;"
```

### **Logs Analysis**
```bash
# Tenant-spezifische Logs suchen
dotnet run | grep "Tenant:"

# Channel-Management Logs
dotnet run | grep "TenantChannelManager"

# SMPP Connection Pool Logs pro Tenant
dotnet run | grep "SmppChannel.*tenant"
```

## 📈 **Performance Testing**

### **Concurrent Tenant Requests**
```bash
# Test gleichzeitige Requests von verschiedenen Tenants
# Tenant A
curl -k -X POST "https://localhost:7142/api/message/send" \
  -H "X-Subscription-Key: dev-tenant-a-12345" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49111", "Content": "Concurrent Test A"}' &

# Tenant B
curl -k -X POST "https://localhost:7142/api/message/send" \
  -H "X-Subscription-Key: dev-tenant-b-67890" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49222", "Content": "Concurrent Test B"}' &

# Tenant C
curl -k -X POST "https://localhost:7142/api/message/send" \
  -H "X-Subscription-Key: dev-tenant-c-http-99999" \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber": "+49333", "Content": "Concurrent Test C"}' &

wait
```

### **Connection Pool Verification**
- Tenant A: Sollte 3 SMPP-Verbindungen mit SystemId "tenant_a" öffnen
- Tenant B: Sollte 2 SMPP-Verbindungen mit SystemId "tenant_b" öffnen  
- Tenant C: Keine SMPP-Verbindungen (nur HTTP)

## 🎯 **Expected Results**

### **Successful Multi-Tenant Setup**
1. **Database**: 3 Tenants mit verschiedenen Channel-Konfigurationen
2. **SMPP Simulator**: Empfängt Nachrichten von tenant_a und tenant_b SystemIds
3. **HTTP Channels**: Erfolgreiche Posts zu httpbin.org und postman-echo.com
4. **Tenant Isolation**: Jeder Tenant sieht nur eigene Nachrichten in GET-Requests
5. **Channel Selection**: Tenant A kann sowohl SMPP als auch HTTP verwenden

### **Performance Benchmarks**
- **SMPP Nachrichten**: ~228ms pro SMS (gleich wie Single-Tenant)
- **HTTP Nachrichten**: ~160ms pro SMS (für Error-Handling) 
- **Concurrent Tenants**: Keine Performance-Degradation zwischen Tenants
- **Memory Usage**: Lineare Skalierung pro Tenant (kein Speicherleak)

## 🔄 **Zurück zu Single-Tenant Mode**
```bash
# Deaktiviere Multi-Tenant Mode
# In appsettings.Development.json:
{
  "MultiTenantSettings": {
    "EnableMultiTenant": false
  }
}

# Oder verwende Standard Development-Konfiguration
cp appsettings.json appsettings.Development.json
```

---

**Mit diesem Setup kannst du die komplette Multi-Tenant-Funktionalität lokal testen und validieren!** 🎉