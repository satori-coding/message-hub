# Scripts and Tools

This directory contains various scripts, tools and test files for the MessageHub SMS service.

## 🔧 Development Scripts

### `kill-processes.sh`
Kills any running MessageHub processes and checks if the required ports (5289, 7142) are free.

```bash
./kill-processes.sh
```

### `test_sms.sh`
Tests the SMS API by sending a test SMS and checking the database contents.

```bash
./test_sms.sh
```

### `view_db.py`
Python script to view the contents of the SMS database (SQLite).

```bash
python3 view_db.py
```

## 📨 API Testing

### `api-tests.http`
HTTP test requests for VS Code REST Client extension. Contains various test scenarios:
- Send SMS via direct API
- Test different phone number formats
- Error handling tests
- Status queries

**Usage**: Install "REST Client" extension in VS Code, then click "Send Request" above each test.

## 📁 Test Data and Documentation

### `test-sendsms-commands.txt`
Collection of curl commands for testing the SMS API.

### `dlr-testing-scenarios.txt`
Test scenarios for Delivery Receipt (DLR) functionality.

### `add_dlr_fields.sql`
SQL script to add delivery receipt fields to the database schema.

### `claude_instructions.md`
Instructions and context for Claude Code assistant.

### `README-Testing.md`
Detailed testing documentation and procedures.

### `info.txt`
General project information and notes.

### `commands/`
Directory containing various development commands and utilities.

## ⚠️ Deprecated/Legacy Scripts

### `test_queue.py` ❌ (Veraltet)
Python script for testing Azure Service Bus functionality. 
**Status**: Deprecated - Service Bus integration was removed from the project.

### `send_to_queue.sh` ❌ (Veraltet)
Shell script for sending messages to Service Bus queue.
**Status**: Deprecated - Service Bus integration was removed from the project.

## 🚀 Quick Start

1. **Kill any running processes**: `./kill-processes.sh`
2. **Start the application**: `cd .. && dotnet run`
3. **Test SMS functionality**: `./test_sms.sh`
4. **View database contents**: `python3 view_db.py`

## 📋 Requirements

- **Python 3**: For database viewing scripts
- **curl**: For HTTP API testing
- **VS Code with REST Client extension**: For interactive API testing
- **.NET 8**: For running the main application

## 🛠️ Usage from Project Root

All scripts are designed to be run from the scripts directory:

```bash
cd scripts
./test_sms.sh
python3 view_db.py
```

Some scripts automatically change to the correct directory, but it's recommended to run them from the `scripts/` folder.