using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageHub.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenantSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SubscriptionKey = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantChannelConfiguration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ChannelType = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfigurationType = table.Column<string>(type: "TEXT", maxLength: 34, nullable: false),
                    ApiUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    AuthUsername = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    AuthPassword = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    FromNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    RequestTimeout = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: true),
                    WebhookUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    SystemId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Password = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    MaxConnections = table.Column<int>(type: "INTEGER", nullable: true),
                    ConnectionTimeout = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    BindTimeout = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    SubmitTimeout = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    ApiTimeout = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    KeepAliveInterval = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    ExpectDeliveryReceipts = table.Column<bool>(type: "INTEGER", nullable: true),
                    DeliveryReceiptTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    TimeoutStatus = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantChannelConfiguration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantChannelConfiguration_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId",
                table: "Messages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantChannelConfiguration_TenantId_ChannelName",
                table: "TenantChannelConfiguration",
                columns: new[] { "TenantId", "ChannelName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_SubscriptionKey",
                table: "Tenants",
                column: "SubscriptionKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Tenants_TenantId",
                table: "Messages",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Tenants_TenantId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "TenantChannelConfiguration");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "TenantName",
                table: "Messages");
        }
    }
}
