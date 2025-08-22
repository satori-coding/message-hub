using MessageHub.Channels.Shared;

namespace MessageHub.Services;

/// <summary>
/// Command message for MassTransit queue processing
/// Contains all information needed to process a message in the background
/// </summary>
public record SendMessageCommand(
    string PhoneNumber,
    string Content,
    ChannelType ChannelType,
    int? TenantId,
    string? ChannelName,
    int MessageId  // DB Message ID for processing
);