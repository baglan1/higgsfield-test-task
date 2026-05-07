using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MemoryService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:memory_type", "fact,preference,opinion,event")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "turns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    messages = table.Column<string>(type: "jsonb", nullable: false),
                    raw_text = table.Column<string>(type: "text", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_turns", x => x.id);
                    table.ForeignKey(
                        name: "FK_turns_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<int>(type: "memory_type", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    predicate = table.Column<string>(type: "text", nullable: true),
                    @object = table.Column<string>(name: "object", type: "text", nullable: true),
                    aspect = table.Column<string>(type: "text", nullable: true),
                    stance = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    confidence = table.Column<float>(type: "real", nullable: false, defaultValue: 0.7f),
                    source_turn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    supersedes = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memories", x => x.id);
                    table.ForeignKey(
                        name: "FK_memories_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memories_turns_source_turn_id",
                        column: x => x.source_turn_id,
                        principalTable: "turns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memory_edges",
                columns: table => new
                {
                    src_memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation = table.Column<string>(type: "text", nullable: false),
                    dst_memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_edges", x => new { x.src_memory_id, x.relation, x.dst_memory_id });
                    table.ForeignKey(
                        name: "FK_memory_edges_memories_dst_memory_id",
                        column: x => x.dst_memory_id,
                        principalTable: "memories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_edges_memories_src_memory_id",
                        column: x => x.src_memory_id,
                        principalTable: "memories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memories_session_id",
                table: "memories",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_memories_source_turn_id",
                table: "memories",
                column: "source_turn_id");

            migrationBuilder.CreateIndex(
                name: "memories_embedding_hnsw",
                table: "memories",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:ef_construction", 64)
                .Annotation("Npgsql:StorageParameter:m", 16);

            migrationBuilder.CreateIndex(
                name: "memories_subject_trgm",
                table: "memories",
                column: "subject")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "memories_user_active",
                table: "memories",
                columns: new[] { "user_id", "active" });

            migrationBuilder.CreateIndex(
                name: "memory_edges_dst",
                table: "memory_edges",
                columns: new[] { "dst_memory_id", "relation" });

            migrationBuilder.CreateIndex(
                name: "sessions_user",
                table: "sessions",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "turns_session",
                table: "turns",
                columns: new[] { "session_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "turns_user",
                table: "turns",
                columns: new[] { "user_id", "ts" });

            // Generated tsvector column for keyword search via ts_rank. Maintained by Postgres.
            migrationBuilder.Sql("""
                ALTER TABLE memories
                ADD COLUMN ts tsvector
                GENERATED ALWAYS AS (to_tsvector('english', text)) STORED;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX memories_ts_gin ON memories USING gin (ts);
                """);

            // Unique partial index enforcing one ACTIVE memory per (user, subject, predicate, aspect).
            // EF can't model COALESCE() in a HasIndex expression, so emit raw SQL here.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX memories_active_triple
                ON memories (user_id, subject, COALESCE(predicate,''), COALESCE(aspect,''))
                WHERE active;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_edges");

            migrationBuilder.DropTable(
                name: "memories");

            migrationBuilder.DropTable(
                name: "turns");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
