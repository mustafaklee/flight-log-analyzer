using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightLog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FlightData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BootTimeUtc",
                table: "Flights",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hardware",
                table: "Flights",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogMetadata",
                table: "Flights",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<long>(
                name: "LogStartTimestampUs",
                table: "Flights",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleType",
                table: "Flights",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FlightEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampUs = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlightEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlightEvents_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlightParameters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    DefaultValue = table.Column<double>(type: "double precision", nullable: true),
                    TimestampUs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlightParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlightParameters_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LogProcessingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    LogFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StoragePrefix = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    AllocatedBytes = table.Column<long>(type: "bigint", nullable: true),
                    ParserName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ParserVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    NormalizationVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    MessageCount = table.Column<long>(type: "bigint", nullable: false),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false),
                    SeriesCount = table.Column<int>(type: "integer", nullable: false),
                    SignalCount = table.Column<int>(type: "integer", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    ParameterCount = table.Column<int>(type: "integer", nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    ChunkBytes = table.Column<long>(type: "bigint", nullable: false),
                    SkippedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    Corrupt = table.Column<bool>(type: "boolean", nullable: false),
                    SummaryTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    Warnings = table.Column<List<string>>(type: "text[]", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogProcessingJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogProcessingJobs_FlightLogFiles_LogFileId",
                        column: x => x.LogFileId,
                        principalTable: "FlightLogFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LogProcessingJobs_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LogProcessingJobs_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FlightSignalSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Instance = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false),
                    FirstTimestampUs = table.Column<long>(type: "bigint", nullable: true),
                    LastTimestampUs = table.Column<long>(type: "bigint", nullable: true),
                    ColumnCount = table.Column<int>(type: "integer", nullable: false),
                    StoragePrefix = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlightSignalSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlightSignalSeries_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlightSignalSeries_LogProcessingJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "LogProcessingJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlightSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Instance = table.Column<int>(type: "integer", nullable: false),
                    Field = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Column = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Multiplier = table.Column<double>(type: "double precision", nullable: true),
                    SourceMessage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourceInstance = table.Column<int>(type: "integer", nullable: true),
                    SourceFields = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Conversion = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NormalizationVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SampleCount = table.Column<long>(type: "bigint", nullable: false),
                    InvalidCount = table.Column<long>(type: "bigint", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: true),
                    Max = table.Column<double>(type: "double precision", nullable: true),
                    FirstTimestampUs = table.Column<long>(type: "bigint", nullable: true),
                    LastTimestampUs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlightSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlightSignals_FlightSignalSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "FlightSignalSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlightSignals_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignalChunks",
                columns: table => new
                {
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    StartTimestampUs = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestampUs = table.Column<long>(type: "bigint", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalChunks", x => new { x.SeriesId, x.Index });
                    table.ForeignKey(
                        name: "FK_SignalChunks_FlightSignalSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "FlightSignalSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlightEvents_FlightId_TimestampUs",
                table: "FlightEvents",
                columns: new[] { "FlightId", "TimestampUs" });

            migrationBuilder.CreateIndex(
                name: "IX_FlightParameters_FlightId_Name",
                table: "FlightParameters",
                columns: new[] { "FlightId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_FlightSignals_FlightId_Name",
                table: "FlightSignals",
                columns: new[] { "FlightId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlightSignals_SeriesId",
                table: "FlightSignals",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightSignalSeries_FlightId",
                table: "FlightSignalSeries",
                column: "FlightId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightSignalSeries_JobId",
                table: "FlightSignalSeries",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_LogProcessingJobs_FlightId",
                table: "LogProcessingJobs",
                column: "FlightId");

            migrationBuilder.CreateIndex(
                name: "IX_LogProcessingJobs_LogFileId_StartedAt",
                table: "LogProcessingJobs",
                columns: new[] { "LogFileId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LogProcessingJobs_TeamId",
                table: "LogProcessingJobs",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlightEvents");

            migrationBuilder.DropTable(
                name: "FlightParameters");

            migrationBuilder.DropTable(
                name: "FlightSignals");

            migrationBuilder.DropTable(
                name: "SignalChunks");

            migrationBuilder.DropTable(
                name: "FlightSignalSeries");

            migrationBuilder.DropTable(
                name: "LogProcessingJobs");

            migrationBuilder.DropColumn(
                name: "BootTimeUtc",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "Hardware",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "LogMetadata",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "LogStartTimestampUs",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "VehicleType",
                table: "Flights");
        }
    }
}
