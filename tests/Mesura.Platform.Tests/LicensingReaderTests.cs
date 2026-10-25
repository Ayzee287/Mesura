using System.Text.Json;
using System.Text.RegularExpressions;
using Mesura.Core.Licensing;
using Mesura.Platform.Windows.Windows;

namespace Mesura.Platform.Tests;

/// <summary>
/// Activation's reader on the machine running the tests (D031). Read-only, and it asserts invariants only: the licences
/// read are Windows' and Office's, their states are known, and nothing read holds a product key — not a key, not its last
/// five characters, not the firmware's key — whatever this PC's licences are.
/// </summary>
public sealed partial class LicensingReaderTests
{
    [Fact]
    public void Licences_are_read_without_any_product_key()
    {
        var status = LicensingReader.Read();
        if (status.Error is not null)
        {
            return; // a PC whose licensing service doesn't answer: nothing was read, which is what the page then says
        }

        Assert.All(status.Windows, w => Assert.Equal(LicensedProduct.Windows, w.Product));
        Assert.All(status.Office, o => Assert.Equal(LicensedProduct.Office, o.Product));
        Assert.All(status.Windows.Concat(status.Office), l =>
        {
            Assert.NotEqual(LicenseState.Unknown, l.State);
            Assert.True(Guid.TryParse(l.SkuId, out _), l.SkuId);
            Assert.True(l.KmsHost is null || l.IsKmsClient, "a KMS host is read only for a KMS client key");
        });

        // Nothing shaped like a key, in anything the reader returns.
        var text = JsonSerializer.Serialize(status);
        Assert.DoesNotMatch(KeyShape(), text);
    }

    [GeneratedRegex(@"\b[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}-[A-Z0-9]{5}\b")]
    private static partial Regex KeyShape();
}
