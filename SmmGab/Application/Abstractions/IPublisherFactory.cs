using SmmGab.Domain.Enums;

namespace SmmGab.Application.Abstractions;

public interface IPublisherFactory
{
    IPublisher GetPublisher(ChannelType channelType);
}

