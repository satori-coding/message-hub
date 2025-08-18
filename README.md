# MessageHub - Multi-Channel SMS Service

A production-ready ASP.NET Core 8.0 SMS service with modular channel architecture supporting SMPP and HTTP/REST SMS providers.

## 🚀 Features

- **Multi-Channel Architecture**: SMPP and HTTP/REST SMS channels
- **Connection Pooling**: 8x performance improvement for SMPP
- **Real Delivery Receipts**: Complete SMS lifecycle tracking
- **Production-Ready**: Enhanced retry logic and error handling
- **Extensible**: Easy to add new SMS providers (Twilio, AWS SNS, etc.)
- **Database Support**: SQLite (dev) and Azure SQL Server (prod)
- **Azure Integration**: Key Vault and Application Insights ready

## 📁 Project Structure

```
MessageHub/                     # Main API project
├── Controllers/               # REST API controllers
├── Services/                  # Business logic services
└── DomainModels/             # Data models and database context

MessageHub.Shared/             # Common interfaces and types
├── ISmsChannel.cs            # Channel interface and enums

MessageHub.SmppChannel/        # SMPP SMS channel
├── SmppChannel.cs            # SMPP implementation with pooling
├── SmppConnection.cs         # Connection management
└── ServiceCollectionExtensions.cs

MessageHub.HttpSmsChannel/     # HTTP/REST SMS channel
├── HttpSmsChannel.cs         # HTTP SMS implementation
├── HttpSmsChannelConfiguration.cs # Provider templates
└── ServiceCollectionExtensions.cs

scripts/                       # Helper scripts and tests
├── api-tests.http            # API testing scenarios
├── performance-test.sh       # Performance benchmarks
└── view_db.py               # Database inspection tool
```

## ⚙️ Setup Instructions

### 1. Clone and Configure

```bash
git clone https://github.com/satori-coding/message-hub.git
cd message-hub

# Copy example configuration
cp appsettings.Example.json appsettings.Development.json
```

### 2. Configure Settings

Edit `appsettings.Development.json` with your credentials:

```json
{
  "SmppSettings": {
    "Host": "your-smpp-host.com",
    "SystemId": "your-username",
    "Password": "your-password"
  },
  "HttpSmsChannel": {
    "ProviderName": "Twilio",
    "ApiKey": "your-twilio-auth-token",
    "FromNumber": "+1234567890"
  }
}
```

### 3. Run the Service

```bash
# Restore packages
dotnet restore

# Run in development mode
dotnet run

# Access Swagger UI
open https://localhost:7142/swagger
```

## 🔧 Development Setup

### Prerequisites
- .NET 8.0 SDK
- SQLite (for local development)
- SMPP Simulator (optional, for testing)

### SMPP Testing with Local Simulator

1. **Install SMPPSim on Linux:**
```bash
# Download and setup SMPPSim
wget http://www.seleniumsoftware.com/downloads/SMPPSim.tar.gz
tar -xzf SMPPSim.tar.gz
cd SMPPSim
./startsmppsim.sh
```

2. **Configure for localhost:**
```json
{
  "SmppSettings": {
    "Host": "localhost",
    "Port": 2775,
    "SystemId": "smppclient1",
    "Password": "password"
  }
}
```

## 🌐 API Endpoints

### Send SMS
```bash
POST /api/sms/send
Content-Type: application/json

{
  "PhoneNumber": "+49123456789",
  "Content": "Hello World!",
  "ChannelType": "SMPP"  // Optional: "SMPP" or "HTTP"
}
```

### Get SMS Status
```bash
GET /api/sms/{id}/status
```

**Response includes complete delivery receipt data:**
```json
{
  "id": 1,
  "phoneNumber": "+49123456789",
  "status": "Delivered",
  "providerMessageId": "sm_abc123",
  "deliveredAt": "2024-08-18T15:30:00Z",
  "deliveryStatus": "DELIVRD",
  "channelType": "SMPP"
}
```

## 🏗️ Architecture

### Multi-Channel SMS Flow

```
API Request → Channel Selection → SMS Service → Channel Implementation
    ↓              ↓                  ↓              ↓
/api/sms/send → ChannelType → SmsService → SmppChannel/HttpChannel
    ↓              ↓                  ↓              ↓
Database ← Status Updates ← Provider Response ← SMPP/HTTP API
```

### Performance Benchmarks

- **SMPP Connection Pool**: 8x faster (243ms vs 2000ms)
- **First SMS**: 427ms (connection setup)
- **Subsequent SMS**: 243ms (connection reuse)
- **Success Rate**: 100% under load testing

## 📊 Production Deployment

### Environment Variables

Create environment-specific configurations:

**Production (`appsettings.Production.json`):**
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=your-sql-server;Database=MessageHub;..."
  },
  "KeyVaultEndpoint": "https://your-keyvault.vault.azure.net/"
}
```

### Azure Key Vault Setup

Store sensitive settings in Azure Key Vault:
- `SmppSettings--Password`
- `ConnectionStrings--DefaultConnection`
- `ApplicationInsights--ConnectionString`

### Health Checks

The service includes health check endpoints:
- SMPP connection health monitoring
- Database connectivity checks
- Channel-specific health validation

## 🧪 Testing

### Run API Tests
```bash
# Using VS Code REST Client
# Open scripts/api-tests.http and execute requests

# Performance testing
chmod +x scripts/performance-test.sh
./scripts/performance-test.sh
```

### View Database
```bash
# Inspect SQLite database
python3 scripts/view_db.py
```

## 🔐 Security Notes

⚠️ **Never commit sensitive data to Git:**
- `appsettings.Development.json` is gitignored
- Use `appsettings.Example.json` as template
- Store production secrets in Azure Key Vault
- Database files (*.db) are automatically ignored

## 📈 Monitoring

### Application Insights Integration
- SMS sending metrics and performance
- Error tracking with SMPP-specific context
- Connection pool health monitoring
- Delivery receipt processing analytics

### Logging
Structured logging with correlation IDs for complete SMS lifecycle tracking.

## 🤝 Contributing

1. Fork the repository
2. Create feature branch: `git checkout -b feature/new-sms-provider`
3. Make changes and test thoroughly
4. Submit pull request with detailed description

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## 🚀 Quick Start Summary

```bash
# 1. Clone and setup
git clone https://github.com/satori-coding/message-hub.git
cd message-hub
cp appsettings.Example.json appsettings.Development.json

# 2. Edit your credentials in appsettings.Development.json

# 3. Run the service
dotnet run

# 4. Test with curl
curl -X POST https://localhost:7142/api/sms/send \
  -H "Content-Type: application/json" \
  -d '{"PhoneNumber":"+49123456789","Content":"Test SMS"}'
```

🎉 **Your multi-channel SMS service is now ready!**