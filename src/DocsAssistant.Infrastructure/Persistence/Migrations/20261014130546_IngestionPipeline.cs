using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocsAssistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IngestionPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "processing_started_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "documents",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "document_contents",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_contents", x => x.document_id);
                    table.ForeignKey(
                        name: "fk_document_contents_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_contents_tenant_id",
                table: "document_contents",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_contents");

            migrationBuilder.DropColumn(
                name: "processing_started_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "documents");
        }
    }
}
