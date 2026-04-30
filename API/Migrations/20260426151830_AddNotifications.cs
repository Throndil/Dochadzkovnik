using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotificationsEnabled",
                table: "Employees",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppEnabled",
                table: "Employees",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppNumber",
                table: "Employees",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NoActivity48hEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NoActivity48hTime = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    WorkingDaysOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManagerSummaryEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManagerSummaryEmployeeId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastTickAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VapidPublicKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    VapidPrivateKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    VapidSubject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EmployeeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TriggerType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    TriggerDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EmployeeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    P256dhKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AuthKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_EmployeeId_Channel_TriggerType_TriggerDate",
                table: "NotificationLogs",
                columns: new[] { "EmployeeId", "Channel", "TriggerType", "TriggerDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_EmployeeId_TriggerDate",
                table: "NotificationLogs",
                columns: new[] { "EmployeeId", "TriggerDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_EmployeeId",
                table: "PushSubscriptions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationConfigs");

            migrationBuilder.DropTable(
                name: "NotificationLogs");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "NotificationsEnabled",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "WhatsAppEnabled",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "WhatsAppNumber",
                table: "Employees");
        }
    }
}
