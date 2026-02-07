using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class MovieConfiguration : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> builder)
    {
        builder.ToTable("movies");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(m => m.Title)
            .HasColumnName("title")
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(m => m.Description)
            .HasColumnName("description")
            .HasMaxLength(2000);

        builder.Property(m => m.ReleaseYear)
            .HasColumnName("release_year")
            .IsRequired();

        builder.Property(m => m.DurationMinutes)
            .HasColumnName("duration_minutes")
            .IsRequired();

        builder.Property(m => m.FilePath)
            .HasColumnName("file_path")
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(m => m.XxHash)
            .HasColumnName("xx_hash")
            .HasMaxLength(64);

        builder.Property(m => m.FileSizeBytes)
            .HasColumnName("file_size_bytes")
            .IsRequired();

        builder.Property(m => m.Verified)
            .HasColumnName("verified")
            .IsRequired();

        builder.Property(m => m.StorageConnectionString)
            .HasColumnName("storage_connection_string")
            .HasMaxLength(2000);

        builder.Property(m => m.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(m => m.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Indexes
        builder.HasIndex(m => m.Title);
        builder.HasIndex(m => m.ReleaseYear);
        builder.HasIndex(m => m.Verified);

        // One-to-many relationship with Subtitles
        builder.HasMany(m => m.Subtitles)
            .WithOne(s => s.Movie)
            .HasForeignKey(s => s.MovieId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
