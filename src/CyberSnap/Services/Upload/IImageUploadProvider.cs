using System.IO;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload;

public interface IImageUploadProvider
{
    /// <summary>CyberGems / Imgur / ImgBB / Custom. Custom protocol implementations all return <see cref="UploadProviderKind.Custom"/>.</summary>
    UploadProviderKind Kind { get; }

    /// <summary>
    /// For <see cref="UploadProviderKind.Custom"/> only: which protocol this implementation handles.
    /// CyberGems/Imgur/ImgBB return null (dispatch uses Kind alone).
    /// </summary>
    UploadCustomProtocol? CustomProtocol { get; }

    bool IsConfigured(UploadRuntimeConfig config);

    Task<UploadResult> UploadAsync(
        Stream imageStream,
        string contentType,
        string fileName,
        long contentLength,
        UploadRuntimeConfig config,
        IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken);
}
