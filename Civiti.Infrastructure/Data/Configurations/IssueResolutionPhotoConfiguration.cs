using Civiti.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Civiti.Infrastructure.Data.Configurations;

public class IssueResolutionPhotoConfiguration : IEntityTypeConfiguration<IssueResolutionPhoto>
{
    public void Configure(EntityTypeBuilder<IssueResolutionPhoto> builder)
    {
        builder.HasKey(p => p.Id);

        // Same width as IssuePhoto.Url and IssueValidationLimits.MaxPhotoUrlLength: both hold
        // the same Supabase public URLs, written by the same client upload pipeline.
        builder.Property(p => p.Url)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(p => p.Description)
            .HasMaxLength(500);

        builder.Property(p => p.DisplayOrder)
            .HasDefaultValue(0);

        builder.HasIndex(p => p.IssueId);

        builder.HasOne(p => p.Issue)
            .WithMany(i => i.ResolutionPhotos)
            .HasForeignKey(p => p.IssueId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
