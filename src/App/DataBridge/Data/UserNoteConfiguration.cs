using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class UserNoteConfiguration : IEntityTypeConfiguration<UserNoteEntity>
{
    public void Configure(EntityTypeBuilder<UserNoteEntity> builder)
    {
        builder.ToTable("user_notes", "notes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.OwnerSubject).HasColumnName("owner_subject").HasMaxLength(255).IsRequired();
        builder.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(32).IsRequired();
        builder.Property(x => x.TargetId).HasColumnName("target_id").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(8192).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedOnAdd().IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(x => new { x.OwnerSubject, x.TargetType, x.TargetId })
            .IsUnique()
            .HasDatabaseName("ux_user_notes_owner_target");

        builder.HasIndex(x => new { x.OwnerSubject, x.UpdatedAt })
            .HasDatabaseName("ix_user_notes_owner_updated_at");
    }
}
