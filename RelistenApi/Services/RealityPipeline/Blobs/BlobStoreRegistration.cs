using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Relisten.RealityPipeline.Blobs;

/// Configuration section "Blobs":
///   Provider          "s3" (production: Cloudflare R2) or "filesystem"
///   Root              filesystem root (Provider = filesystem)
///   S3:ServiceUrl, S3:Bucket, S3:AccessKeyId, S3:SecretAccessKey, S3:Region
///   ZstdLevel         default 19
public static class BlobStoreRegistration
{
    public static IServiceCollection AddRealityBlobs(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var section = configuration.GetSection("Blobs");
        var provider = section["Provider"] ?? "filesystem";
        services.AddSingleton<IBlobStore>(_ =>
            provider switch
            {
                "s3" => new S3BlobStore(
                    section.GetSection("S3").Get<S3BlobStoreOptions>()
                        ?? throw new InvalidOperationException(
                            "Blobs:S3 is required when Blobs:Provider is s3"
                        )
                ),
                "filesystem" => new FileSystemBlobStore(
                    section["Root"] ?? Path.Combine(Path.GetTempPath(), "relisten-blobs")
                ),
                _ => throw new InvalidOperationException($"Unknown Blobs:Provider '{provider}'"),
            }
        );
        services.AddSingleton(new BlobCodec(section.GetValue("ZstdLevel", BlobCodec.DefaultLevel)));
        services.AddSingleton<FeatureBlobStore>();
        services.AddSingleton<RealityBlobStore>();
        return services;
    }
}
