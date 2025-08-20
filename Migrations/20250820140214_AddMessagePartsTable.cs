﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageHub.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagePartsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageParts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    PartNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalParts = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveryReceiptText = table.Column<string>(type: "TEXT", nullable: true),
                    DeliveryStatus = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<int>(type: "INTEGER", nullable: true),
                    NetworkErrorCode = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageParts_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageParts_MessageId_PartNumber",
                table: "MessageParts",
                columns: new[] { "MessageId", "PartNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageParts_ProviderMessageId",
                table: "MessageParts",
                column: "ProviderMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageParts");
        }
    }
}
