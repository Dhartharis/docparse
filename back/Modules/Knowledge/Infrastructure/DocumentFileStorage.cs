using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs.Models;
using DocParse.Modules.Knowledge.Application;

namespace DocParse.Modules.Knowledge.Infrastructure;

public sealed class DocumentFileStorage : IOriginalFileStorage
{
    private readonly BlobContainerClient _container;

    public DocumentFileStorage(BlobServiceClient blobServiceClient, IConfiguration configuration)
    {
        var containerName = configuration["AzureStorage:ContainerName"] ?? "documents";
        _container = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public async Task<string> UploadAsync(Guid documentId, Stream content, string extension, string contentType, CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobName = $"{documentId:N}/original{extension}";
        var blob = _container.GetBlobClient(blobName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            }
        };

        await blob.UploadAsync(content, options, cancellationToken);
        return blobName;
    }

    public async Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var response = await _container.GetBlobClient(blobName).DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }
    public async Task DeletePrefixAsync(Guid id,CancellationToken ct){if(!await _container.ExistsAsync(ct))return;await foreach(var blob in _container.GetBlobsAsync(BlobTraits.None,BlobStates.None,prefix:id.ToString("N")+"/",cancellationToken:ct))await _container.DeleteBlobIfExistsAsync(blob.Name,cancellationToken:ct);}

}
