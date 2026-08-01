using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Civiti.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueResolutionRewardedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ResolutionRewardedAt",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill. Every issue already sitting in Resolved (IssueStatus.Resolved = 5) paid out
            // under the old once-per-resolution rule, so stamp it as rewarded. Without this, the
            // first re-open of any historically resolved issue would hand out a second 100 points
            // when it was resolved again. UpdatedAt is the closest record of when that happened.
            migrationBuilder.Sql(
                """
                UPDATE "Issues"
                SET "ResolutionRewardedAt" = "UpdatedAt"
                WHERE "Status" = 5;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolutionRewardedAt",
                table: "Issues");
        }
    }
}
