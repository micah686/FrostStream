using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class SubtitleConfiguration : IEntityTypeConfiguration<Subtitle>
{
    public void Configure(EntityTypeBuilder<Subtitle> builder)
    {
        builder.ToTable("subtitles");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(s => s.MovieId)
            .HasColumnName("movie_id")
            .IsRequired();

        builder.Property(s => s.Language)
            .HasColumnName("language")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(s => s.FilePath)
            .HasColumnName("file_path")
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(s => s.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Indexes
        builder.HasIndex(s => new { s.MovieId, s.Language }).IsUnique();
    }
}
