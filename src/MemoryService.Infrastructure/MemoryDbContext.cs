using MemoryService.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace MemoryService.Infrastructure;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Embedding dimension baked into the generated migration. Changing this requires a new migration
    /// (or running an ALTER COLUMN). Set to OpenAI <c>text-embedding-3-small</c>'s default.
    /// </summary>
    public const int EmbeddingDim = 1536;

    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Turn> Turns => Set<Turn>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryEdge> MemoryEdges => Set<MemoryEdge>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");
        b.HasPostgresExtension("pg_trgm");
        b.HasPostgresEnum<MemoryType>(name: "memory_type");

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.UserId).HasColumnName("user_id").HasColumnType("text");
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(s => s.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.HasIndex(s => new { s.UserId, s.CreatedAt }).HasDatabaseName("sessions_user");
        });

        b.Entity<Turn>(e =>
        {
            e.ToTable("turns");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.SessionId).HasColumnName("session_id");
            e.Property(t => t.UserId).HasColumnName("user_id").HasColumnType("text");
            e.Property(t => t.Timestamp).HasColumnName("ts");
            e.Property(t => t.MessagesJson).HasColumnName("messages").HasColumnType("jsonb");
            e.Property(t => t.RawText).HasColumnName("raw_text");
            e.Property(t => t.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.HasOne<Session>()
                .WithMany()
                .HasForeignKey(t => t.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => new { t.SessionId, t.Timestamp }).HasDatabaseName("turns_session");
            e.HasIndex(t => new { t.UserId,    t.Timestamp }).HasDatabaseName("turns_user");
        });

        b.Entity<Memory>(e =>
        {
            e.ToTable("memories");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id");
            e.Property(m => m.UserId).HasColumnName("user_id").HasColumnType("text");
            e.Property(m => m.SessionId).HasColumnName("session_id");
            e.Property(m => m.Type).HasColumnName("type").HasColumnType("memory_type");
            e.Property(m => m.Subject).HasColumnName("subject").HasColumnType("text");
            e.Property(m => m.Predicate).HasColumnName("predicate").HasColumnType("text");
            e.Property(m => m.Object).HasColumnName("object").HasColumnType("text");
            e.Property(m => m.Aspect).HasColumnName("aspect").HasColumnType("text");
            e.Property(m => m.Stance).HasColumnName("stance").HasColumnType("text");
            e.Property(m => m.Text).HasColumnName("text").HasColumnType("text");
            e.Property(m => m.Embedding).HasColumnName("embedding").HasColumnType($"vector({EmbeddingDim})");
            e.Property(m => m.Confidence).HasColumnName("confidence").HasColumnType("real").HasDefaultValue(0.7f);
            e.Property(m => m.SourceTurnId).HasColumnName("source_turn_id");
            e.Property(m => m.Active).HasColumnName("active").HasDefaultValue(true);
            e.Property(m => m.Supersedes).HasColumnName("supersedes");
            e.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(m => m.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            e.Property(m => m.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

            e.HasOne<Session>()
                .WithMany()
                .HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Turn>()
                .WithMany()
                .HasForeignKey(m => m.SourceTurnId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(m => m.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasStorageParameter("m", 16)
                .HasStorageParameter("ef_construction", 64)
                .HasDatabaseName("memories_embedding_hnsw");

            e.HasIndex(m => new { m.UserId, m.Active })
                .HasDatabaseName("memories_user_active");

            e.HasIndex(m => m.Subject)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("memories_subject_trgm");
        });

        b.Entity<MemoryEdge>(e =>
        {
            e.ToTable("memory_edges");
            e.HasKey(x => new { x.SrcMemoryId, x.Relation, x.DstMemoryId });
            e.Property(x => x.SrcMemoryId).HasColumnName("src_memory_id");
            e.Property(x => x.Relation).HasColumnName("relation").HasColumnType("text");
            e.Property(x => x.DstMemoryId).HasColumnName("dst_memory_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

            e.HasOne<Memory>()
                .WithMany()
                .HasForeignKey(x => x.SrcMemoryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Memory>()
                .WithMany()
                .HasForeignKey(x => x.DstMemoryId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.DstMemoryId, x.Relation }).HasDatabaseName("memory_edges_dst");
        });
    }
}
