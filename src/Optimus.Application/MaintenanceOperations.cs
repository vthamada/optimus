using Optimus.Domain;

namespace Optimus.Application;

public static class MaintenanceOperations
{
    public static bool TryParseIntegrity(string token, out IntegrityOperation operation)
    {
        operation = token switch
        {
            "sfc-verify" => IntegrityOperation.SfcVerifyOnly,
            "dism-check" => IntegrityOperation.DismCheckHealth,
            "dism-scan" => IntegrityOperation.DismScanHealth,
            _ => (IntegrityOperation)(-1)
        };
        return Enum.IsDefined(operation);
    }
}
