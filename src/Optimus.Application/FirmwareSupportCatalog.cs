using Optimus.Domain;

namespace Optimus.Application;

/// <summary>Official landing pages only. Matching a vendor is not matching a firmware image or proving an update exists.</summary>
public static class FirmwareSupportCatalog
{
    public static IReadOnlyList<FirmwareSupportReference> FromEvidence(IReadOnlyList<DiagnosticEvidence> evidence)
    {
        List<FirmwareSupportReference> references = [];
        foreach (var item in evidence.Where(item => item.CollectorId == "windows.hardware-profile" && item.Status != EvidenceStatus.Unavailable))
        {
            foreach (var (manufacturerKey, modelKey) in new[]
            {
                ("system.0.Manufacturer", "system.0.Model"),
                ("baseboards.0.Manufacturer", "baseboards.0.Product")
            })
            {
                var manufacturer = UniqueText(item, manufacturerKey);
                var model = UniqueText(item, modelKey);
                if (manufacturer is null || model is null || OfficialUrl(manufacturer) is not { } url) continue;
                if (!references.Any(reference => reference.Url == url && reference.Model == model))
                    references.Add(new(manufacturer, model, url, item.Id));
            }
        }
        return references.ToArray();
    }
    public static string? OfficialUrl(string manufacturer)
    {
        var value = manufacturer.Trim().ToUpperInvariant();
        return value switch
        {
            "DELL INC." or "DELL COMPUTER CORPORATION" or "DELL" => "https://www.dell.com/support/home/",
            "LENOVO" => "https://pcsupport.lenovo.com/",
            "HP" or "HEWLETT-PACKARD" or "HP INC." => "https://support.hp.com/",
            "ASUSTEK COMPUTER INC." or "ASUS" or "ASUSTEK COMPUTER INC" => "https://www.asus.com/support/",
            "MICRO-STAR INTERNATIONAL CO., LTD." or "MSI" => "https://www.msi.com/support",
            "GIGABYTE TECHNOLOGY CO., LTD." or "GIGABYTE" => "https://www.gigabyte.com/Support",
            "ASROCK" or "ASROCK INC." => "https://www.asrock.com/support/",
            "ACER" => "https://www.acer.com/support",
            _ => null
        };
    }
    private static string? UniqueText(DiagnosticEvidence evidence, string key)
    {
        var values = evidence.Facts.Where(fact => fact.Key == key).Take(2).ToArray();
        return values.Length == 1 && values[0].TextValue is { Length: > 0 and <= 1024 } value ? value : null;
    }
}
