using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Civiti.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueResolutionNotifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ResolutionNotifiedAt",
                table: "Issues",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill. Every issue already sitting in Resolved (IssueStatus.Resolved = 5)
            // has already had its supporters notified under the old rule, where Resolved was
            // absorbing and the fan-out could therefore only ever happen once. Stamping them
            // keeps an issue resolved in the hours just before this deploys from mailing its
            // followers a second time if it is re-opened and re-resolved inside the cooldown.
            // UpdatedAt is the closest record of when that fan-out went out.
            migrationBuilder.Sql(
                """
                UPDATE "Issues"
                SET "ResolutionNotifiedAt" = "UpdatedAt"
                WHERE "Status" = 5;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolutionNotifiedAt",
                table: "Issues");
        }
    }
}
