namespace Mailbox.Pst.Messaging;

/// <summary>
/// The property ids this reader acts on, numbered as [MS-OXPROPS] does. Only what the messaging
/// layer itself interprets belongs here — everything else a file carries still comes through as
/// a <see cref="PstProperty"/>, id and all, for the importer to read by number.
/// </summary>
internal static class Pid
{
    // The store.
    public const ushort RecordKey = 0x0FF9;
    public const ushort IpmSubTreeEntryId = 0x35E0;
    public const ushort DisplayName = 0x3001;

    // Folders.
    public const ushort ContainerClass = 0x3613;
    public const ushort ContentCount = 0x3602;
    public const ushort ContentUnreadCount = 0x3603;
    public const ushort Subfolders = 0x360A;

    // Messages.
    public const ushort MessageClass = 0x001A;
    public const ushort Subject = 0x0037;
    public const ushort ClientSubmitTime = 0x0039;
    public const ushort TransportHeaders = 0x007D;
    public const ushort SenderName = 0x0C1A;
    public const ushort SenderEmailAddress = 0x0C1F;
    public const ushort SenderSmtpAddress = 0x5D01;
    public const ushort FlagStatus = 0x1090;
    public const ushort MessageDeliveryTime = 0x0E06;
    public const ushort MessageFlags = 0x0E07;
    public const ushort MessageSize = 0x0E08;
    public const ushort Body = 0x1000;
    public const ushort Html = 0x1013;
    public const ushort InternetMessageId = 0x1035;

    // Recipients.
    public const ushort RecipientType = 0x0C15;
    public const ushort AddressType = 0x3002;
    public const ushort EmailAddress = 0x3003;
    public const ushort SmtpAddress = 0x39FE;

    // Attachments.
    public const ushort AttachData = 0x3701;
    public const ushort AttachFilename = 0x3704;
    public const ushort AttachMethod = 0x3705;
    public const ushort AttachLongFilename = 0x3707;
    public const ushort AttachMimeTag = 0x370E;
    public const ushort AttachContentId = 0x3712;
}
