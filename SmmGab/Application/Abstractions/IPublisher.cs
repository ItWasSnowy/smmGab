using SmmGab.Domain.Models;

namespace SmmGab.Application.Abstractions;

public interface IPublisher
{
    Task<PublishResult> PublishAsync(
        PublicationTarget target, 
        Publication publication, 
        Channel channel, 
        CancellationToken cancellationToken);
}

public class PublishResult
{
    public bool Success { get; set; }
    public bool IsPermanentError { get; set; }
    public string? ErrorMessage { get; set; }
}

