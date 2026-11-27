using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Data;

public partial class CmsDbContext
{
    public DbSet<ClubLogoEntity> ClubLogos => Set<ClubLogoEntity>();

    private static void ConfigureBranding(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClubLogoEntity>(entity =>
        {
            entity.ToTable("ClubLogos");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Data).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(e => e.UpdatedBy).HasMaxLength(64);
        });
    }
}

/// <summary>
/// The club logo (themes phase): one row (Id 1) or none. Data are the uploaded bytes, checked by Branding/LogoImage:
/// PNG or JPEG, at most 512 KB and 1024 x 1024 px. ContentType comes from the bytes, never from the upload.
/// </summary>
public class ClubLogoEntity
{
    public const int SingleId = 1;

    public int Id { get; set; } = SingleId;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    /// <summary>image/png or image/jpeg.</summary>
    public string ContentType { get; set; } = string.Empty;
    /// <summary>SHA-256 of Data, 64 lower-case hex: the PCs download the logo again only when it changes.</summary>
    public string Sha256 { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
