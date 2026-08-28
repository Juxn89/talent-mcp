using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Talent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    skill_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    years_of_experience = table.Column<int>(type: "integer", nullable: false),
                    seniority = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    willing_to_relocate = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    required_skill_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    seniority = table.Column<string>(type: "text", nullable: false),
                    city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    arrangement = table.Column<string>(type: "text", nullable: false),
                    salary_min = table.Column<int>(type: "integer", nullable: false),
                    salary_max = table.Column<int>(type: "integer", nullable: false),
                    salary_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_candidates_seniority",
                table: "candidates",
                column: "seniority");

            migrationBuilder.CreateIndex(
                name: "ix_candidates_status",
                table: "candidates",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_arrangement",
                table: "jobs",
                column: "arrangement");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_title",
                table: "jobs",
                column: "title");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidates");

            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
