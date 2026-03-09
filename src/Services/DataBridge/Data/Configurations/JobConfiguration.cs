using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
    }
}
