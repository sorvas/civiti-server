using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Civiti.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueResolutionPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IssueResolutionPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueResolutionPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueResolutionPhotos_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssueResolutionPhotos_IssueId",
                table: "IssueResolutionPhotos",
                column: "IssueId");

            // Backfill. Every issue already sitting in Resolved (IssueStatus.Resolved = 5) got
            // there before this column existed, and a null ResolvedAt on a Resolved issue reads
            // to the clients as "resolved, date unknown" — which would leave the resolved banner
            // dateless across the entire existing corpus. UpdatedAt is the closest record of when
            // it happened, and is what the ResolutionRewardedAt backfill used for the same reason.
            //
            // Only Resolved rows are stamped: the column's contract is that it is null whenever
            // the issue is not currently Resolved, so a previously-resolved-then-re-opened issue
            // must stay null rather than claim a resolution it has already backed out of.
            migrationBuilder.Sql(
                """
                UPDATE "Issues"
                SET "ResolvedAt" = "UpdatedAt"
                WHERE "Status" = 5;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueResolutionPhotos");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Issues");
        }
    }
}
