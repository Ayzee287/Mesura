namespace Mesura.Core.Raw;

/// <summary>
/// The <c>smbios</c> section: the SMBIOS structure table (architecture §4.4 rule 2). Parsing happens
/// in the normaliser. Serial numbers, asset tags, the system UUID and OEM strings are overwritten at
/// collection (architecture §13); see <c>Privacy.SmbiosScrubber</c> for the exact list.
/// </summary>
/// <param name="MajorVersion">The SMBIOS major version.</param>
/// <param name="MinorVersion">The SMBIOS minor version.</param>
/// <param name="DmiRevision">The DMI revision.</param>
/// <param name="Table">The structure table without the platform's header, base64 in JSON.</param>
/// <param name="FieldsScrubbed">How many string and binary fields were overwritten.</param>
/// <param name="ZeroedFromOffset">
/// If the table could not be walked to its end, the offset from which every byte was zeroed,
/// because fields there could not be located and scrubbed. <c>null</c> when the whole table was walked.
/// </param>
public sealed record SmbiosData(
    int MajorVersion,
    int MinorVersion,
    int DmiRevision,
    byte[] Table,
    int FieldsScrubbed,
    int? ZeroedFromOffset);
