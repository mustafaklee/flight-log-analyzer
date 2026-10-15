using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightLog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiAnalyses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResponseModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ContextVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ContextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Context = table.Column<string>(type: "text", nullable: true),
                    ContextSummary = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    WorkerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProviderCalls = table.Column<int>(type: "integer", nullable: false),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    StopReason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RawOutput = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: true),
                    Report = table.Column<string>(type: "jsonb", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiAnalyses_AnalysisRuns_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "AnalysisRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiAnalyses_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiAnalyses_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiAnalyses_AnalysisRunId",
                table: "AiAnalyses",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAnalyses_FlightId_CreatedAt",
                table: "AiAnalyses",
                columns: new[] { "FlightId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiAnalyses_Status_CreatedAt",
                table: "AiAnalyses",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiAnalyses_TeamId_CreatedAt",
                table: "AiAnalyses",
                columns: new[] { "TeamId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiAnalyses");
        }
    }
}
