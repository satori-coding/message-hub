#!/usr/bin/env python3
import sqlite3
import os

db_path = "sms_database.db"

if not os.path.exists(db_path):
    print(f"Database file {db_path} not found!")
    exit(1)

try:
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    
    # Show all tables
    print("=== All Tables in Database ===")
    cursor.execute("SELECT name FROM sqlite_master WHERE type='table';")
    tables = cursor.fetchall()
    for table in tables:
        print(f"- {table[0]}")
    
    # Check Messages table structure
    print("\n=== Messages Table Structure ===")
    cursor.execute("PRAGMA table_info(Messages);")
    columns = cursor.fetchall()
    for col in columns:
        print(f"  {col[1]} ({col[2]}) - Required: {not col[3]}")
    
    # Check MessageParts table structure
    print("\n=== MessageParts Table Structure ===")
    cursor.execute("PRAGMA table_info(MessageParts);")
    columns = cursor.fetchall()
    for col in columns:
        print(f"  {col[1]} ({col[2]}) - Required: {not col[3]}")
    
    # Check foreign key constraints
    print("\n=== Foreign Key Constraints ===")
    cursor.execute("PRAGMA foreign_key_list(MessageParts);")
    fks = cursor.fetchall()
    for fk in fks:
        print(f"  {fk[3]} -> {fk[2]}.{fk[4]} (ON DELETE {fk[6]})")
    
    # Check indexes
    print("\n=== Indexes on MessageParts ===")
    cursor.execute("SELECT name, sql FROM sqlite_master WHERE type='index' AND tbl_name='MessageParts';")
    indexes = cursor.fetchall()
    for idx in indexes:
        if idx[1]:  # Skip auto-created primary key indexes
            print(f"  {idx[0]}: {idx[1]}")
    
    # Count current data
    print("\n=== Current Data Count ===")
    cursor.execute("SELECT COUNT(*) FROM Messages;")
    message_count = cursor.fetchone()[0]
    print(f"Messages: {message_count}")
    
    cursor.execute("SELECT COUNT(*) FROM MessageParts;")
    parts_count = cursor.fetchone()[0]
    print(f"MessageParts: {parts_count}")
    
    if message_count > 0:
        print("\n=== Sample Messages ===")
        cursor.execute("SELECT Id, Recipient, Status, MessageParts, ProviderMessageId FROM Messages ORDER BY CreatedAt DESC LIMIT 5;")
        messages = cursor.fetchall()
        for msg in messages:
            print(f"  ID {msg[0]}: {msg[1]} - Status: {msg[2]} - Parts: {msg[3]} - Provider ID: {msg[4]}")
    
    conn.close()
    print("\n✅ Database schema verification completed successfully!")
    
except Exception as e:
    print(f"❌ Error: {e}")