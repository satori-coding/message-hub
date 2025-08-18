#!/usr/bin/env python3
import sqlite3
import os

db_path = "../sms_database.db"

if not os.path.exists(db_path):
    print(f"Database file {db_path} not found!")
    exit(1)

try:
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    
    # Show all tables
    print("=== Tables in database ===")
    cursor.execute("SELECT name FROM sqlite_master WHERE type='table';")
    tables = cursor.fetchall()
    for table in tables:
        print(f"- {table[0]}")
    
    print("\n=== SmsMessages Table Content ===")
    
    # Get table info
    cursor.execute("PRAGMA table_info(SmsMessages);")
    columns = cursor.fetchall()
    print("Columns:", [col[1] for col in columns])
    
    # Get all data
    cursor.execute("SELECT * FROM SmsMessages ORDER BY CreatedAt DESC;")
    rows = cursor.fetchall()
    
    if not rows:
        print("No data found in SmsMessages table.")
    else:
        print(f"\nFound {len(rows)} records:")
        print("-" * 120)
        
        # Header
        headers = [col[1] for col in columns]
        print(" | ".join(f"{h:15}" for h in headers))
        print("-" * 120)
        
        # Data rows
        for row in rows:
            print(" | ".join(f"{str(val):15}" for val in row))
    
    conn.close()
    
except Exception as e:
    print(f"Error: {e}")