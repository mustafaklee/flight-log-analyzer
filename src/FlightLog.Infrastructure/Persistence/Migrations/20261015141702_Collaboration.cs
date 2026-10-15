using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightLog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Collaboration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Tags",
                table: "Flights",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FindingReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartUs = table.Column<long>(type: "bigint", nullable: false),
                    EndUs = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Flagged = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FindingReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FindingReviews_AspNetUsers_AssigneeId",
                        column: x => x.AssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FindingReviews_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FindingReviews_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FlightComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingReviewId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StartUs = table.Column<long>(type: "bigint", nullable: true),
                    EndUs = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlightComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlightComments_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FlightComments_FindingReviews_FindingReviewId",
                        column: x => x.FindingReviewId,
                        principalTable: "FindingReviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlightComments_Flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "Flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlightComments_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Flights_Tags",
                table: "Flights",
                column: "Tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_CreatedAt",
                table: "AuditEntries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EntityType_EntityId",
                table: "AuditEntries",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_FlightId_CreatedAt",
                table: "AuditEntries",
                columns: new[] { "FlightId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TeamId_CreatedAt",
                table: "AuditEntries",
                columns: new[] { "TeamId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FindingReviews_AssigneeId",
                table: "FindingReviews",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_FindingReviews_FlightId_RuleId_StartUs_EndUs",
                table: "FindingReviews",
                columns: new[] { "FlightId", "RuleId", "StartUs", "EndUs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FindingReviews_TeamId_AssigneeId_UpdatedAt",
                table: "FindingReviews",
                columns: new[] { "TeamId", "AssigneeId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FindingReviews_TeamId_Status_UpdatedAt",
                table: "FindingReviews",
                columns: new[] { "TeamId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FlightComments_AuthorId",
                table: "FlightComments",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightComments_FindingReviewId",
                table: "FlightComments",
                column: "FindingReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightComments_FlightId_CreatedAt",
                table: "FlightComments",
                columns: new[] { "FlightId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FlightComments_TeamId",
                table: "FlightComments",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "FlightComments");

            migrationBuilder.DropTable(
                name: "FindingReviews");

            migrationBuilder.DropIndex(
                name: "IX_Flights_Tags",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Flights");
        }
    }
}
